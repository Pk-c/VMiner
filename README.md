# VMiner

VMiner reads Japanese text directly from the screen on Windows. Hold the capture key,
move the mouse to the opposite corner of the desired area, then release the key. No mouse
click is required.

Version 2 is a native **C# / .NET 10 / WPF** application distributed as a portable folder.
Screen capture, OCR, furigana, Japanese-to-English translation, and vocabulary mining all
run locally.

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

On first launch, VMiner asks where to create the JSON vocabulary database. Its location can
later be changed from the main window. Each entry contains a Japanese dictionary form, its
reading, an English definition, and any number of Japanese sentence / English translation
pairs. Existing words are merged with new examples, while identical sentence pairs are not
duplicated.

The **Collection** tab can search every field, edit words and their examples, or remove a
complete entry. Double-clicking an entry also opens the editor.

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
folders. The `src` folder and build script are only required for development.

## Architecture

| Component | Local implementation |
| --- | --- |
| Interface | WPF on .NET 10 |
| Capture | Win32 hooks and in-memory GDI capture |
| OCR | Japanese OCR built into Windows, with 4× image scaling |
| Furigana | Kawazu / MeCab |
| Segmentation | MeCab IPA with dictionary forms and readings |
| Translation | TranslateGemma GGUF through LLamaSharp |
| Collection | User-selected JSON file |
| Configuration | `config.json` next to the executable |

The main project is located in `src\VMiner.App`. Captures and analysis stay in memory; no
temporary screenshots are written to disk.
