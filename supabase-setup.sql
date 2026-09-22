-- Run this script in the Supabase SQL Editor.
-- It creates the relational vocabulary schema and migrates the former JSONB table,
-- when present, before removing that obsolete table.

begin;

create table if not exists public.vocabulary_entries (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null references auth.users(id) on delete cascade,
    word text not null check (btrim(word) <> ''),
    reading text not null check (btrim(reading) <> ''),
    definition text not null check (btrim(definition) <> ''),
    wanikani_subject_id bigint,
    wanikani_updated_at timestamptz,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    unique (user_id, word, reading)
);

alter table public.vocabulary_entries
    add column if not exists wanikani_subject_id bigint,
    add column if not exists wanikani_updated_at timestamptz;

create unique index if not exists vocabulary_entries_wanikani_subject
on public.vocabulary_entries (user_id, wanikani_subject_id)
where wanikani_subject_id is not null;

create table if not exists public.sentence_examples (
    id uuid primary key default gen_random_uuid(),
    vocabulary_entry_id uuid not null
        references public.vocabulary_entries(id) on delete cascade,
    japanese text not null check (btrim(japanese) <> ''),
    english text not null default '',
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    unique (vocabulary_entry_id, japanese, english)
);

create or replace function public.set_vocabulary_updated_at()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
    new.updated_at = now();
    return new;
end;
$$;

drop trigger if exists set_vocabulary_entries_updated_at
    on public.vocabulary_entries;
create trigger set_vocabulary_entries_updated_at
before update on public.vocabulary_entries
for each row execute function public.set_vocabulary_updated_at();

drop trigger if exists set_sentence_examples_updated_at
    on public.sentence_examples;
create trigger set_sentence_examples_updated_at
before update on public.sentence_examples
for each row execute function public.set_vocabulary_updated_at();

alter table public.vocabulary_entries enable row level security;
alter table public.sentence_examples enable row level security;

revoke all on table public.vocabulary_entries from anon;
revoke all on table public.sentence_examples from anon;
grant select, insert, update, delete on table public.vocabulary_entries to authenticated;
grant select, insert, update, delete on table public.sentence_examples to authenticated;

drop policy if exists "Users manage their own vocabulary entries"
    on public.vocabulary_entries;
create policy "Users manage their own vocabulary entries"
on public.vocabulary_entries
for all
to authenticated
using ((select auth.uid()) = user_id)
with check ((select auth.uid()) = user_id);

drop policy if exists "Users manage examples for their vocabulary"
    on public.sentence_examples;
create policy "Users manage examples for their vocabulary"
on public.sentence_examples
for all
to authenticated
using (exists (
    select 1
    from public.vocabulary_entries entry
    where entry.id = sentence_examples.vocabulary_entry_id
      and entry.user_id = (select auth.uid())
))
with check (exists (
    select 1
    from public.vocabulary_entries entry
    where entry.id = sentence_examples.vocabulary_entry_id
      and entry.user_id = (select auth.uid())
));

-- Add a word and, optionally, one example as a single atomic operation.
create or replace function public.add_or_update_vocabulary(
    p_word text,
    p_reading text,
    p_definition text,
    p_japanese text default '',
    p_english text default ''
)
returns uuid
language plpgsql
security invoker
set search_path = ''
as $$
declare
    entry_id uuid;
begin
    if btrim(coalesce(p_word, '')) = '' or
       btrim(coalesce(p_reading, '')) = '' or
       btrim(coalesce(p_definition, '')) = '' then
        raise exception 'Word, reading, and definition are required.';
    end if;

    insert into public.vocabulary_entries (user_id, word, reading, definition)
    values ((select auth.uid()), btrim(p_word), btrim(p_reading), btrim(p_definition))
    on conflict (user_id, word, reading)
    do update set definition = excluded.definition
    returning id into entry_id;

    if btrim(coalesce(p_japanese, '')) <> '' then
        insert into public.sentence_examples (
            vocabulary_entry_id, japanese, english)
        values (entry_id, btrim(p_japanese), btrim(coalesce(p_english, '')))
        on conflict (vocabulary_entry_id, japanese, english) do nothing;
    end if;

    return entry_id;
end;
$$;

-- Replace one entry and its examples atomically. Other entries are untouched.
create or replace function public.replace_vocabulary_entry(
    p_entry_id uuid,
    p_word text,
    p_reading text,
    p_definition text,
    p_examples jsonb default '[]'::jsonb
)
returns void
language plpgsql
security invoker
set search_path = ''
as $$
begin
    if btrim(coalesce(p_word, '')) = '' or
       btrim(coalesce(p_reading, '')) = '' or
       btrim(coalesce(p_definition, '')) = '' then
        raise exception 'Word, reading, and definition are required.';
    end if;

    update public.vocabulary_entries
    set word = btrim(p_word),
        reading = btrim(p_reading),
        definition = btrim(p_definition)
    where id = p_entry_id
      and user_id = (select auth.uid());

    if not found then
        raise exception 'The vocabulary entry no longer exists.';
    end if;

    delete from public.sentence_examples
    where vocabulary_entry_id = p_entry_id;

    insert into public.sentence_examples (
        vocabulary_entry_id, japanese, english)
    select p_entry_id,
           btrim(example->>'japanese'),
           btrim(coalesce(example->>'english', ''))
    from (
        select distinct value as example
        from jsonb_array_elements(coalesce(p_examples, '[]'::jsonb))
    ) examples
    where btrim(coalesce(example->>'japanese', '')) <> ''
    on conflict (vocabulary_entry_id, japanese, english) do nothing;
end;
$$;

-- Merge a bounded WaniKani batch without overwriting user-edited definitions.
create or replace function public.import_wanikani_vocabulary(p_entries jsonb)
returns jsonb
language plpgsql
security invoker
set search_path = ''
as $$
declare
    imported jsonb;
    example jsonb;
    entry_id uuid;
    current_user_id uuid := (select auth.uid());
    subject_id bigint;
    word_value text;
    reading_value text;
    definition_value text;
    inserted_count integer;
    new_entries integer := 0;
    existing_entries integer := 0;
    new_examples integer := 0;
begin
    if current_user_id is null then
        raise exception 'Sign in before importing WaniKani vocabulary.';
    end if;
    if jsonb_typeof(coalesce(p_entries, 'null'::jsonb)) <> 'array' then
        raise exception 'The WaniKani import payload must be an array.';
    end if;
    if jsonb_array_length(p_entries) > 250 then
        raise exception 'A WaniKani import batch cannot exceed 250 entries.';
    end if;

    for imported in select value from jsonb_array_elements(p_entries)
    loop
        entry_id := null;
        subject_id := (imported->>'subject_id')::bigint;
        word_value := btrim(coalesce(imported->>'word', ''));
        reading_value := btrim(coalesce(imported->>'reading', ''));
        definition_value := btrim(coalesce(imported->>'definition', ''));
        if subject_id is null or word_value = '' or reading_value = '' or
           definition_value = '' then
            raise exception 'A WaniKani entry is incomplete.';
        end if;

        select id into entry_id
        from public.vocabulary_entries
        where user_id = current_user_id
          and wanikani_subject_id = subject_id;

        if entry_id is null then
            select id into entry_id
            from public.vocabulary_entries
            where user_id = current_user_id
              and word = word_value
              and reading = reading_value;
        end if;

        if entry_id is null then
            insert into public.vocabulary_entries (
                user_id, word, reading, definition,
                wanikani_subject_id, wanikani_updated_at)
            values (
                current_user_id, word_value, reading_value, definition_value,
                subject_id, nullif(imported->>'updated_at', '')::timestamptz)
            returning id into entry_id;
            new_entries := new_entries + 1;
        else
            update public.vocabulary_entries
            set wanikani_subject_id = coalesce(wanikani_subject_id, subject_id),
                wanikani_updated_at = greatest(
                    wanikani_updated_at,
                    nullif(imported->>'updated_at', '')::timestamptz)
            where id = entry_id
              and user_id = current_user_id;
            existing_entries := existing_entries + 1;
        end if;

        for example in
            select value
            from jsonb_array_elements(coalesce(imported->'examples', '[]'::jsonb))
        loop
            if btrim(coalesce(example->>'japanese', '')) <> '' then
                insert into public.sentence_examples (
                    vocabulary_entry_id, japanese, english)
                values (
                    entry_id,
                    btrim(example->>'japanese'),
                    btrim(coalesce(example->>'english', '')))
                on conflict (vocabulary_entry_id, japanese, english) do nothing;
                get diagnostics inserted_count = row_count;
                new_examples := new_examples + inserted_count;
            end if;
        end loop;
    end loop;

    return jsonb_build_object(
        'new_entries', new_entries,
        'existing_entries', existing_entries,
        'new_examples', new_examples);
end;
$$;

revoke all on function public.add_or_update_vocabulary(text, text, text, text, text)
    from public, anon;
revoke all on function public.replace_vocabulary_entry(uuid, text, text, text, jsonb)
    from public, anon;
revoke all on function public.import_wanikani_vocabulary(jsonb)
    from public, anon;
grant execute on function public.add_or_update_vocabulary(text, text, text, text, text)
    to authenticated;
grant execute on function public.replace_vocabulary_entry(uuid, text, text, text, jsonb)
    to authenticated;
grant execute on function public.import_wanikani_vocabulary(jsonb)
    to authenticated;

-- One-time migration from VMiner's former per-user JSONB document.
do $$
begin
    if to_regclass('public.vocabulary_databases') is not null then
        insert into public.vocabulary_entries (user_id, word, reading, definition)
        select distinct on (database.user_id, legacy.word, legacy.reading)
               database.user_id,
               legacy.word,
               legacy.reading,
               legacy.definition
        from public.vocabulary_databases database
        cross join lateral (
            select btrim(coalesce(value->>'Word', value->>'word', '')) as word,
                   btrim(coalesce(value->>'Reading', value->>'reading', '')) as reading,
                   btrim(coalesce(value->>'Definition', value->>'definition', '')) as definition,
                   ordinality
            from jsonb_array_elements(
                coalesce(database.data->'Entries', database.data->'entries', '[]'::jsonb))
                with ordinality
        ) legacy
        where legacy.word <> ''
          and legacy.reading <> ''
          and legacy.definition <> ''
        order by database.user_id, legacy.word, legacy.reading, legacy.ordinality desc
        on conflict (user_id, word, reading)
        do update set definition = excluded.definition;

        insert into public.sentence_examples (
            vocabulary_entry_id, japanese, english)
        select distinct entry.id,
               btrim(coalesce(example.value->>'Japanese', example.value->>'japanese', '')),
               btrim(coalesce(example.value->>'English', example.value->>'english', ''))
        from public.vocabulary_databases database
        cross join lateral jsonb_array_elements(
            coalesce(database.data->'Entries', database.data->'entries', '[]'::jsonb))
            legacy(value)
        join public.vocabulary_entries entry
          on entry.user_id = database.user_id
         and entry.word = btrim(coalesce(legacy.value->>'Word', legacy.value->>'word', ''))
         and entry.reading = btrim(coalesce(legacy.value->>'Reading', legacy.value->>'reading', ''))
        cross join lateral jsonb_array_elements(
            coalesce(legacy.value->'Examples', legacy.value->'examples', '[]'::jsonb))
            example(value)
        where btrim(coalesce(example.value->>'Japanese', example.value->>'japanese', '')) <> ''
        on conflict (vocabulary_entry_id, japanese, english) do nothing;

        drop table public.vocabulary_databases;
    end if;
end;
$$;

commit;
