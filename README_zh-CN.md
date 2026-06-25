# ClashOfRim 第三方兼容包

[English](README.md)

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

## 什么时候需要做兼容

只有当第三方模组改变了需要在多人投影、流转或服务端结算中保持一致的数据时，才应该为它添加 ClashOfRim 兼容。能不介入的模组应尽量不介入。

以下情况通常需要客户端兼容：

- 模组在 pawn、thing、地图、lord、世界对象或 comp 上保存了只对原本本地运行会话有效的运行时状态；
- 模组添加了自定义容器、库存、链接储存、载具、穿梭机或其他持有物品的对象，且其中内容不能被摊平成普通地图散落物；
- 模组为可交易物品、礼物、商店商品、pawn 包、尸体、雕塑、奖杯、武器、书籍、基因包、异种胚芽等对象添加了影响流转的自定义元数据；
- 模组添加了能以远程殖民地为目标的大地图进入、降落、远行队、载具或穿梭机逻辑；
- 模组改变了远程地图上的 AI 或 lord 行为，可能导致投影出的 pawn 攻击、离开、偷窃、搬运、维修或清理错误的状态；
- pawn 或 thing 反序列化后需要重建运行时缓存，例如 verb manager、图形缓存、异种外观状态或动画状态。

以下情况通常需要服务端插件：

- 服务器需要索引自定义容器、载具、pawn 持有者或其他嵌套数据；
- 服务器需要采集自定义生命值模型、价格输入、地形或世界数据、陷阱分类器等权威基线；
- 袭击结算需要理解自定义损伤模型、载具部件、载具货物、压缩储存内容，或无法仅靠原版 thing 字段判断的特殊对象；
- 兼容逻辑依赖服务端已加载插件列表，或依赖客户端上传的模组清单。

如果一个模组只是添加普通原版字段定义、翻译、贴图、音效、纯 UI 工具、视觉效果，或只影响不会跨远程地图、流转、快照和结算的本地单人行为，通常不需要为它编写兼容。

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

服务器插件会构建到 `Build\ServerPlugins`，并由主仓库的服务端打包脚本复制到服务器包的 `Plugins/` 目录。

### Adaptive Storage Server Plugin

插件 ID：`AIRsLight.ClashOfRim.AdaptiveStorage`

为 Adaptive Storage 容器内容添加存档索引扩展，让服务器可以识别容器内物品，而不是把它们当作普通地图散落物处理。

### Vehicle Framework Server Plugin

插件 ID：`AIRsLight.ClashOfRim.VehicleFramework`

为载具和载具货物添加服务端索引、载具生命值基线要求，以及袭击结算时的快照编辑能力。

## 范围说明

该兼容包只处理会影响 ClashOfRim 多人状态的行为，例如远程地图投影、pawn/物品流转、存档索引、基线校验和袭击结算。受支持第三方模组的普通单人行为仍由对应模组自身负责。
