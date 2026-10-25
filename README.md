# 吱咿 Zhiji（Slay the Spire 2 Mod）

双形态角色：常态吱咿 → 觉醒红神。包含搏动、无休止饥饿、自助餐、戏服替身、流血、卑贱之物、召唤血肉/眷属等机制。

## 构建

需要 .NET SDK 8 与游戏的程序集（`sts2.dll` 等）。构建命令：

```
DOTNET_EnableWriteXorExecute=0 DOTNET_GCHeapHardLimit=0x20000000 DOTNET_gcServer=0 \
dotnet /usr/lib/dotnet/sdk/8.0.131/Roslyn/bincore/csc.dll @build.rsp
```

产物：`zhiji.dll`，与 `pkg/zhiji` 一起打包成 mod。

## 目录

- `src/`：Mod 源码（C# / Godot 4.5）
- `pkg/zhiji/`：mod 资源（`zhiji.json`、`art/`）
- `docs/实现参数.md`：实现细节与历次修复记录

## 协作

欢迎提交 Issue / PR。编译产物（`zhiji.dll`）不入库，请从 Release 下载或自行构建。

## License

MIT
