# Attention

本文件是 CodeStable 技能启动必读的项目注意事项入口。所有 CodeStable 子技能开始工作前必须读取它。

## 项目碎片知识

<!-- cs-note managed: 用 cs-note 维护，新条目按下面分节追加 -->

### 编译与构建

### 运行与本地起服务

### 测试

### 命令与脚本陷阱

- ItemComponent 完整命名空间是 `Barotrauma.Items.Components.ItemComponent`（非 `Barotrauma.ItemComponent`），Harmony 补丁需用 `typeof(Item).Assembly.GetType()` 或 `TargetMethod()` 间接引用
- Barotrauma 物品操作（创建/销毁/修改/移动）全部是**服务端权威**的。`Deconstructor.ProcessItem`、`Fabricator.Fabricate` 等方法内首行均有 `if (IsClient) return;` 守卫。Harmony Prefix 拦截此类方法时必须在**服务端**做物品操作、客户端仅放行。物品操作 API：`inputContainer.Inventory.RemoveItem()` + `Entity.Spawner.AddItemToRemoveQueue()` 删除物品；`OutputContainer.Inventory.TryPutItem()` 移入物品

### 路径与目录约定

### 环境变量与凭证

### 其他
