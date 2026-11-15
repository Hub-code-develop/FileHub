# 本目录是仓库自带的离线 NuGet 源

`CodeHub-FluentUI.3.1.0-codehub.1.nupkg` 来自
[`Hub-code-develop/CodeHub-FluentUI`](https://github.com/Hub-code-develop/CodeHub-FluentUI)
（FluentAvalonia 3.x 的 CodeHub 分支，**MIT** 许可证）。该版本尚未发布到 nuget.org，
为了让 clone 之后可以直接 `dotnet restore` / `./build.sh`，把构建产物随仓库一起分发。

- 目标框架：`net10.0`，依赖 Avalonia `12.1.0`
- 根目录 `NuGet.Config` 通过相对路径 `local-feed` 引用本目录，无需任何本机配置
- 如果之后该包发布到 nuget.org，删掉 `NuGet.Config` 里的 `codehub-local` 一行，
  再删除本目录即可
- 需要升级版本时：替换这里的 nupkg，并同步修改
  `src/Cobalt.Desktop/Cobalt.Desktop.csproj` 里的 `PackageReference` 版本
