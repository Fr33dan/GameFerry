# LZMA SDK decoder

These files are the LZMA and LZMA2 decoders from Igor Pavlov's LZMA SDK 26.04 (2026-10-05). They come from the `C/` folder of https://github.com/ip7z/7zip at commit `9128b80e3a471108678e2b3ec3c985861c8d7b0f`. They are unchanged and in the public domain.

They are compiled into the WebAssembly build. In the browser they unpack `.7z` archives about twice as fast as a managed LZMA decoder. `../gflzma.c` is the small wrapper that C# calls.
