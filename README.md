# LyricsScraper

A lightweight .NET console tool for extracting clean, text-only lyrics (or other main content) from web pages for **personal, ad-free viewing and printing**. It isn't perfect, but it gets most of the lyrics without having to wade through all of the ads. 

---

## ⚙️ Features
- Downloads any public webpage via `HttpClient`
- Uses **HtmlAgilityPack** for parsing and cleanup
- Automatically detects likely lyric blocks (e.g., `[Verse]`, `[Chorus]`, `[Bridge]`)
- Removes scripts, ads, headers, and site boilerplate
- Outputs clean `.txt` files with preserved line breaks
- Works cross-platform (Windows, Linux, macOS)

---

## 🧰 Requirements
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- Internet connection
- (Optional) Git for cloning

---

## 🚀 Usage

```bash
dotnet run -- "https://example.com/some-lyrics-page" --out "hey-joe.txt"
