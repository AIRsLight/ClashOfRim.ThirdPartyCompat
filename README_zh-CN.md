# ClashOfRim 第三方兼容包

这是 ClashOfRim 的可选兼容包，用于处理在远程地图、物品流转、pawn 流转和袭击结算中需要额外适配的 RimWorld 第三方模组。它与主模组分开发行，这样只有在对应第三方模组存在时才启用相关补丁。

兼容包包含两部分：

- 一个 RimWorld 客户端兼容模组，用于注册客户端兼容钩子；
- 可选服务器插件，用于扩展存档索引、基线采集和袭击结算逻辑。

## 加载顺序

该兼容包应加载在 ClashOfRim 主模组之后，并尽量加载在需要兼容的第三方模组之后：

1. Harmony
2. ClashOfRim
3. 支持的第三方模组按其自身加载顺序规则加载
4. ClashOfRim Third-Party Compatibility

兼容包会在运行时检测支持的模组。若某个支持模组未启用，对应兼容钩子会被跳过。

## 客户端兼容范围

### Adaptive Storage Framework

包名：`adaptive.storage.framework`

为压缩存储容器添加远程地图投影处理。在加载其他玩家地图作为远程地图时，兼容层会保留存储容器和其内部物品之间的关系，并标记这些远程容器内容，避免本地 pawn 将其当作普通散落物品处理。

### Vehicle Framework

包名：`SmashPhil.VehicleFramework`

为载具相关的远程地图和袭击流程添加支持。当前钩子覆盖载具远行队进入远程地图、飞行载具降落保护、载具防御点行为，以及袭击中的载具突击行为。该兼容包也提供服务端处理，用于载具存档索引、载具生命值基线、载具货物和袭击结算损伤。

### Melee Animation

包名：`co.uk.epicguru.meleeanimation`

清理远程地图投影和快照保存中的临时近战动画运行时状态，避免远程地图加载到属于其他客户端运行时会话的陈旧动画数据。

### Vanilla Expanded Framework

包名：`oskarpotocki.vanillafactionsexpanded.core`

为框架持有的世界权威状态和运行时状态添加保护，并在 pawn 流转或 pawn 预览恢复后重建 MVCF pawn verb manager。启用 Vanilla Expanded Framework 时，这能让转移后的 pawn 尽量保持原有战斗行为。

### Humanoid Alien Races

包名：`erdelf.HumanoidAlienRaces`

为会保留异种 pawn 外观状态的雕像类对象注册元数据支持。当这些对象通过 ClashOfRim 物品引用进行上架、交易、赠送或恢复时，会保留相关状态。

### Facial Animation

包名：`Nals.FacialAnimation`

为会保留面部动画状态的雕像类对象注册元数据支持。该兼容复用 Humanoid Alien Races 使用的雕像流转路径。

## 服务器插件

服务器插件会构建到 `Build\ServerPlugins`，并由 `Tools\BuildWindowsServer.ps1` 复制到服务器包的 `Plugins/` 目录。

### Adaptive Storage Server Plugin

插件 ID：`AIRsLight.ClashOfRim.AdaptiveStorage`

为 Adaptive Storage 容器内容添加存档索引扩展，让服务器可以识别容器内物品，而不是把它们当作普通地图散落物处理。

### Vehicle Framework Server Plugin

插件 ID：`AIRsLight.ClashOfRim.VehicleFramework`

为载具和载具货物添加服务端索引、载具生命值基线要求，以及袭击结算时的快照编辑能力。

## 范围说明

该兼容包只处理会影响 ClashOfRim 多人状态的行为，例如远程地图投影、pawn/物品流转、存档索引、基线校验和袭击结算。受支持第三方模组的普通单人行为仍由对应模组自身负责。
