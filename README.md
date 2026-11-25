# Read Aloud Studio

**简体中文** · [English](README.en.md)

在 Windows 上按句或整段练习英语朗读。程序在本机识别语音，按英式参考词典给出词和音素反馈，支持中文和英文界面。可以导入 WAV 范读、听词在原句中的读法，也可以单独重练一句，再对照段落原录音。

![跟读练习与全文预览](界面预览.jpg)

## 从源码运行

需要 Windows x64、Git、.NET 10 SDK，以及带 pip 的 Windows x64 CPython 3.11。将一份独立的 Python 安装放在项目的 `runtime\python` 下，使 `runtime\python\python.exe` 可直接运行。以下命令在仓库根目录的 PowerShell 中执行。

```powershell
.\runtime\python\python.exe -m pip install -r .\source\requirements.txt -c .\source\runtime-lock.txt
git apply --directory=runtime/python/Lib/site-packages source/OpenPronounce-en-GB.patch
.\source\Fetch-Models.ps1
.\source\Build.ps1
.\ReadAloudStudio.exe
```

英式评分补丁适用于全新安装的 OpenPronounce 1.1.0，只需应用一次。模型脚本按固定版本下载识别、评分和英音示范模型，校验权重后写入 `engine/models`。准备完成后可以离线练习。默认探测 NVIDIA GPU，也支持 CPU。

## 试一段录音

在「换句 / 换段」中选「雨后的公园」，点「导入练习录音」，打开 `samples/after-the-rain.wav`。想把这段声音用于对照时，可从「示范来源」导入同一份 WAV。

个人录音、范读和练习历史默认保存在 `%LOCALAPPDATA%\ReadAloudStudio`。完整操作见[使用说明](使用说明.txt)，引擎接口和评分实现见[开发说明](source/开发说明.txt)。

## 第三方组件

本项目使用 OpenPronounce 1.1.0 和英式评分补丁、Piper 语音、NAudio 及英式词典。第三方许可与署名见[许可证目录](source/licenses)，模型来源、版本和校验值见[模型清单](source/models.json)。
