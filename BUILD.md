# 构建与安装

## 前置条件
- Windows
- Visual Studio 开发者命令提示符（随 VS 或 Build Tools 安装）

## 构建
在 **VS 开发者命令提示符** 中，进入插件目录执行：

```
cmd.exe /c "dotnet msbuild hdt-tb-record-plugin.csproj /p:Configuration=Release"
```

输出目录：
- `hdt-tb-record-plugin\bin\Release\net472\`

## 安装到 HDT
将生成的插件 DLL 拷贝到：

```
%AppData%\HearthstoneDeckTracker\Plugins
```

然后在 HDT 中：
- 进入 `Options > Tracker > Plugins`
- 启用插件

## 配置与输出
- 配置文件：`%AppData%\HearthstoneDeckTracker\Plugins\hdt-tb-record-plugin\config.json`
- 输出目录：`%AppData%\HearthstoneDeckTracker\Plugins\hdt-tb-record-plugin\records`

可在插件按钮中打开配置文件，或通过插件菜单打开输出目录。


