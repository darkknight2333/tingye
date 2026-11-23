# 第三方语音导入指南

以下路径均相对于 `Tingye.exe`。按需从上游下载，再把相应文件放入同级的 `speech` 目录。每个运行库与模型适用其发布者的许可。

## Breeze TTS 2

1. 下载 `breeze-tts-2-q4_k.gguf`（约 2.54 GB），放到 `speech/breeze-local`。 [Q4_K 模型文件](https://huggingface.co/HoppouAI/Breeze-TTS-2.cpp/blob/main/breeze-tts-2-q4_k.gguf) · [BreezeBlue 模型许可](https://huggingface.co/BreezeBlue/Breeze-TTS-2/blob/main/LICENSE)
2. 解压上游的 Windows x64 Vulkan 版本，将 `breeze-server.exe`、`breeze-cli.exe` 和随包 DLL 放入 `speech/breeze-local/runtime`。将 Windows 版 FFmpeg 的 `ffmpeg.exe` 放到 `speech/breeze-local/media`。 [Breeze-TTS-2.cpp v0.1.7](https://github.com/HoppouAI/Breeze-TTS-2.cpp/releases/tag/v0.1.7) · [FFmpeg 下载](https://ffmpeg.org/download.html#build-windows)
3. 准备一段合成的参考 WAV，或使用你有权使用的音频，并准备与录音内容一致的文字。把参考文件放在 `Tingye.exe` 同目录，命名为 `reference.wav`。在该目录打开 PowerShell，把示例中的参考文字替换成实际内容后执行。 [上游音色保存说明](https://github.com/HoppouAI/Breeze-TTS-2.cpp/blob/v0.1.7/docs/voices.md#making-a-saved-voice)

   ```powershell
   .\speech\breeze-local\runtime\breeze-cli.exe .\speech\breeze-local\breeze-tts-2-q4_k.gguf `
     --ref-audio .\reference.wav --ref-text '这里填写参考音频的完整文字' `
     --voices-dir .\speech\breeze-local\voices --save-voice tingye-clever
   ```

4. 确认 `speech/breeze-local/voices/tingye-clever.breeze` 已生成。重启听页，在「设置 → 朗读 → 朗读语音」选择「机灵女声 · 本地」。如果选项没有出现，检查以下文件是否都放在正确位置。

   ```text
   Tingye.exe
   speech/
     breeze-local/
       breeze-tts-2-q4_k.gguf
       media/ffmpeg.exe
       runtime/breeze-server.exe
       runtime/breeze-cli.exe
       voices/tingye-clever.breeze
   ```

## Kokoro v1.1 中文和英文

1. 下载并完整解压 `kokoro-multi-lang-v1_1.tar.bz2` 到 `speech`。确认 `speech/kokoro-multi-lang-v1_1/model.onnx` 和 `voices.bin` 位于同一层，并保留随包的词典、规则和 `espeak-ng-data`。 [sherpa-onnx 模型包](https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_1.tar.bz2)
2. 获取 sherpa-onnx v1.13.8 Windows x64 shared Release（含 TTS），从同一发行包复制 `sherpa-onnx-c-api.dll`、`onnxruntime.dll` 和随包的 ONNX Runtime 依赖 DLL 到 `speech/runtime`。 [sherpa-onnx v1.13.8](https://github.com/k2-fsa/sherpa-onnx/releases/tag/v1.13.8)
3. 重启后选择 Kokoro 音色或「自动匹配」。

## Qwen3-TTS 1.7B CustomVoice

1. 从支持 Qwen3-TTS 的 KoboldCpp Windows 发行版获取 `koboldcpp-nocuda.exe`，放到 `speech/qwen-local`。 [KoboldCpp 发行版](https://github.com/LostRuins/koboldcpp/releases)
2. 从 KoboldCpp 的模型仓库下载 `Qwen3-TTS-12Hz-1.7B-CustomVoice-Q8_0.gguf` 和 `qwen3-tts-tokenizer-f16.gguf`，按原文件名放到 `speech/qwen-local`。 [GGUF 权重与 tokenizer](https://huggingface.co/koboldcpp/tts/tree/main)
3. 把 Windows 版 `ffmpeg.exe` 放到 `speech/breeze-local/media`。Qwen 和 Breeze 共用这个路径。重启后选择 Serena 或 Vivian。 [FFmpeg 下载](https://ffmpeg.org/download.html#build-windows)
