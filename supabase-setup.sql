-- Run this once in the Supabase SQL Editor for the VMiner project.
create table if not exists public.vocabulary_databases (
    user_id uuid primary key references auth.users(id) on delete cascade,
    data jsonb not null default '{"version":1,"entries":[]}'::jsonb,
    updated_at timestamptz not null default now()
);

alter table public.vocabulary_databases enable row level security;

revoke all on table public.vocabulary_databases from anon;
grant select, insert, update, delete on table public.vocabulary_databases to authenticated;

drop policy if exists "Users manage their own vocabulary" on public.vocabulary_databases;
create policy "Users manage their own vocabulary"
on public.vocabulary_databases
for all
to authenticated
using ((select auth.uid()) = user_id)
with check ((select auth.uid()) = user_id);
