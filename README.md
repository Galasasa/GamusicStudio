# GamusicStudio

GamusicStudio is a Windows desktop application for mapping MIDI keyboards and controllers to computer keyboard actions and playing piano on unused keys.

## Features

- Map MIDI notes and controller signals to keyboard keys and combinations
- Configure velocity ranges, repeated presses, timing, and held keys
- Save named programs and switch between mappings
- Play piano across selected unmapped key ranges with octave and sustain controls
- Add a kick drum echo and switch between mappings and full-keyboard piano using a MIDI note or a MIDI CC control, including buttons, knobs and faders
- Monitor MIDI input, channels, and piano keys in real time

## Build

Install the .NET 6 SDK with Windows Desktop support on Windows.

```powershell
dotnet build MidiKey.csproj -c Release
dotnet publish MidiKey.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The application requires the .NET Desktop Runtime. Piano playback reads the Windows GM sound bank from the local system. Settings are stored under the current user's LocalAppData/MidiKey directory.

## License and assets

The source code is made available under the MIT License. Game names and logos are trademarks of their respective owners. The splash image and game artwork may contain third-party material and are not granted under the MIT License. Confirm the rights to redistribute these assets.

---

# GamusicStudio 键知弹

GamusicStudio 是一款 Windows 桌面软件，可以将 MIDI 键盘与控制器映射为电脑键盘操作，并在未换键的区域弹奏钢琴。

## 功能

- 将 MIDI 音符和控制器信号换成电脑按键或组合键
- 设置力度分层、连按次数、时间间隔与长按
- 保存具名夺舍程序，并在不同方案之间切换
- 在选定的未换键区域弹奏钢琴，调节八度与延音
- 叠加底鼓回声。可用 MIDI 音符或发送 CC 的按钮、旋钮、推杆，在换键方案与全键盘钢琴之间切换
- 实时查看 MIDI 信号、通道和虚拟琴键状态

## 构建

在 Windows 上安装包含 Windows Desktop 支持的 .NET 6 SDK，然后运行

```powershell
dotnet build MidiKey.csproj -c Release
dotnet publish MidiKey.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

运行需要 .NET Desktop Runtime。钢琴音色从本机 Windows GM 音源读取。个人配置保存在当前用户的 LocalAppData/MidiKey 目录。

## 许可证与素材

源码使用 MIT 许可证。游戏名称和标志属于各自的权利人。开屏图片与游戏美术素材可能包含第三方内容，不属于 MIT 许可证的授权范围。重新分发这些素材前请确认相关权利。
