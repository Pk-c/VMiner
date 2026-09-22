# VMiner

VMiner reads Japanese text directly from the screen on Windows. Hold the capture key,
move the mouse to the opposite corner of the desired area, then release the key. No mouse
click is required.

Version 2 is a native **C# / .NET 10 / WPF** application distributed as a portable folder.
Screen capture, OCR, furigana, Japanese-to-English translation, and vocabulary mining all
run locally. Vocabulary collections are synchronized through private Supabase accounts.

## Usage

1. Launch `VMiner.exe`.
2. Place the pointer on one corner of the text area.
3. Hold the configured key (Left Shift by default), move the pointer, then release the key.
4. VMiner displays the Japanese text, furigana, kana reading, and English translation.
5. Hover over and click a word to review its English definition and add it to your collection.

Press Escape or right-click to cancel a selection. **Minimize** keeps VMiner visible in the
taskbar. The Windows close button and **Exit** both shut down the application completely.

## Local translation

The translation engine uses a TranslateGemma 4B model in GGUF format. When the model is
missing, the main screen explains that translation is unavailable and offers to download it
from Hugging Face. The download runs in the background with progress, speed, cancellation,
automatic resume, and a SHA-256 integrity check.

The downloaded model is stored at:

```text
models\translategemma-4b-it-Q4_K_M.gguf
```

The 2.32 GiB model is excluded from version control because of its size and license. Without
it, OCR and furigana remain available. LLamaSharp loads the model through its Vulkan backend;
no captured text is sent over the Internet.

VMiner downloads the Q4_K_M quantization from
[tatsuyaaaaaaa/translategemma-4b-it-gguf](https://huggingface.co/tatsuyaaaaaaa/translategemma-4b-it-gguf).
The model is subject to the [Gemma Terms of Use](https://ai.google.dev/gemma/terms).

English is currently the only translation target.

## Vocabulary collection

Use **Sign in** on the main screen to log in or create a VMiner account with an email address
and password. VMiner restores the encrypted session automatically on future launches and
loads the account's collection from Supabase. **Log out** removes the local session without
deleting the cloud collection.

Each entry contains a Japanese dictionary form, its reading, an English definition, and any
number of Japanese sentence / English translation pairs. Existing words are merged with new
examples, while identical sentence pairs are not duplicated. Supabase stores words and
sentence examples in separate relational tables. VMiner loads the collection into memory at
sign-in, then sends only the entry being changed; it does not keep a local database copy. The
Supabase session and an optional WaniKani token are the only account data stored locally;
both are encrypted for the current Windows user.

When upgrading from a version that used a local JSON database, VMiner imports that collection
once if the Supabase collection is empty. The original local file is left untouched as a
backup.

The **Collection** tab can search every field, edit words and their examples, or remove a
complete entry. Double-clicking an entry also opens the editor.

### Import from WaniKani

Use **Setup** in the WaniKani section of the Collection tab to connect a dedicated read-only
API token and import vocabulary whose lessons have been started. VMiner validates the token,
then encrypts it for the current Windows user with DPAPI and isolates it by VMiner account.
The token is stored only on that computer and is never sent to Supabase.

Use **Update now** whenever you want to synchronize newly studied vocabulary. The button is
enabled only after setup has completed successfully.

Existing VMiner definitions are preserved, identical sentence pairs are skipped, and only
new words or missing WaniKani context sentences are added. Imports are uploaded to Supabase
in bounded batches. WaniKani content remains subject to the user's WaniKani subscription,
terms, and `max_level_granted`; VMiner is not affiliated with WaniKani or Tofugu.

## Supabase setup

The distributed application needs one Supabase project shared by every VMiner account:

1. Create a Supabase project.
2. Open the project's SQL Editor and run `supabase-setup.sql`. It creates the relational
   `vocabulary_entries` and `sentence_examples` tables, enables Row Level Security, and
   restricts every row to its authenticated owner. If the old JSONB table exists, the script
   migrates its contents transactionally before removing it. Rerun the current script when
   upgrading an existing relational installation so the WaniKani import columns and batch
   function are installed.
3. Copy the project URL and **publishable** key from the project's Connect dialog.
4. Copy `supabase-config.example.json` to `supabase-config.json`, replace its placeholders,
   and place it next to `VMiner.exe`.

Never put a Supabase secret or `service_role` key in the desktop application. The publishable
key is specifically designed for distributed clients; database access remains protected by
the included Row Level Security policies. The configured file is excluded from Git. If it is
missing, VMiner reports that Supabase setup is required and keeps account features disabled.

Email/password authentication is enabled by default on hosted Supabase projects. By default,
new users receive a confirmation email before they can sign in.

## Build

Requirements: the .NET 10 SDK on Windows 10 or Windows 11.

```powershell
dotnet build src\VMiner.App\VMiner.App.csproj -c Debug
```

Run the local OCR, furigana, translation, vocabulary, and database checks with:

```powershell
VMiner.exe --self-test .\self-test.txt
Get-Content .\self-test.txt
```

## Build the portable application

```powershell
.\build-portable.ps1
```

The script places `VMiner.exe`, `IpaDic\`, and `runtimes\` directly in the repository root
while preserving the model already stored in `models\`. The resulting application is
self-contained, so the target machine does not need a separate .NET installation.

To distribute VMiner, copy `VMiner.exe` together with the `IpaDic`, `models`, and `runtimes`
folders and your `supabase-config.json` file. The `src` folder, SQL setup file, and build
script are only required for development.

## Architecture

| Component | Local implementation |
| --- | --- |
| Interface | WPF on .NET 10 |
| Capture | Win32 hooks and in-memory GDI capture |
| OCR | Japanese OCR built into Windows, with 4× image scaling |
| Furigana | Kawazu / MeCab |
| Segmentation | MeCab IPA with dictionary forms and readings |
| Translation | TranslateGemma GGUF through LLamaSharp |
| Accounts | Supabase Auth with email and password |
| Collection | Relational Supabase Postgres tables protected by Row Level Security |
| WaniKani import | Read-only WaniKani API v2 client with manual batch synchronization |
| Configuration | `config.json` next to the executable |

The main project is located in `src\VMiner.App`. Captures and analysis stay in memory; no
temporary screenshots or vocabulary database copies are written to disk. Only the encrypted
Supabase refresh session is retained in the current Windows user's local application data.
