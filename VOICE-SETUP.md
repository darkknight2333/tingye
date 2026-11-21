# 第三方语音导入指南

以下路径均相对于 `Tingye.exe`。按需从上游下载，再把相应文件放入同级的 `speech` 目录。每个运行库与模型适用其发布者的许可。

## Kokoro v1.1 中文和英文

1. 下载并完整解压 `kokoro-multi-lang-v1_1.tar.bz2` 到 `speech`。确认 `speech/kokoro-multi-lang-v1_1/model.onnx` 和 `voices.bin` 位于同一层，并保留随包的词典、规则和 `espeak-ng-data`。 [sherpa-onnx 模型包](https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_1.tar.bz2)
2. 获取 sherpa-onnx v1.13.8 Windows x64 shared Release（含 TTS），从同一发行包复制 `sherpa-onnx-c-api.dll`、`onnxruntime.dll` 和随包的 ONNX Runtime 依赖 DLL 到 `speech/runtime`。 [sherpa-onnx v1.13.8](https://github.com/k2-fsa/sherpa-onnx/releases/tag/v1.13.8)
3. 重启后选择 Kokoro 音色或「自动匹配」。

## Breeze TTS 2

1. 下载 `breeze-tts-2-q4_k.gguf` 到 `speech/breeze-local`。 [GGUF 权重](https://huggingface.co/HoppouAI/Breeze-TTS-2.cpp/tree/main) · [BreezeBlue 模型许可](https://huggingface.co/BreezeBlue/Breeze-TTS-2/blob/main/LICENSE)
2. 解压 Breeze-TTS-2.cpp 的 Windows 发行包，把 `breeze-server.exe`、`breeze-cli.exe` 和随包 DLL 放在 `speech/breeze-local/runtime`。下载 Windows 版 FFmpeg，将 `ffmpeg.exe` 放在 `speech/breeze-local/media`。 [Breeze-TTS-2.cpp 发行版](https://github.com/HoppouAI/Breeze-TTS-2.cpp/releases) · [FFmpeg 下载](https://ffmpeg.org/download.html#build-windows)
3. 按上游文档用合成的参考音频或你有权使用的音频与对应文本生成保存音色，命名为 `tingye-clever`。把生成的 `tingye-clever.breeze` 放入 `speech/breeze-local/voices`，重启后选择「机灵女声 · 本地」。 [保存音色的方法](https://github.com/HoppouAI/Breeze-TTS-2.cpp/blob/main/docs/voices.md)

## Qwen3-TTS 1.7B CustomVoice

1. 从支持 Qwen3-TTS 的 KoboldCpp Windows 发行版获取 `koboldcpp-nocuda.exe`，放到 `speech/qwen-local`。 [KoboldCpp 发行版](https://github.com/LostRuins/koboldcpp/releases)
2. 从 KoboldCpp 的模型仓库下载 `Qwen3-TTS-12Hz-1.7B-CustomVoice-Q8_0.gguf` 和 `qwen3-tts-tokenizer-f16.gguf`，按原文件名放到 `speech/qwen-local`。 [GGUF 权重与 tokenizer](https://huggingface.co/koboldcpp/tts/tree/main)
3. 把 Windows 版 `ffmpeg.exe` 放到 `speech/breeze-local/media`。Qwen 和 Breeze 共用这个路径。重启后选择 Serena 或 Vivian。 [FFmpeg 下载](https://ffmpeg.org/download.html#build-windows)
