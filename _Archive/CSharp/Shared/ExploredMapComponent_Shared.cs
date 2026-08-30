using Barotrauma;
using Barotrauma.Items.Components;

namespace ExploredMap.Items
{
    /// <summary>
    /// 探索海图仪的自定义组件（共享端声明）。
    /// 继承 Powered：电池槽的 Voltage 信号直接喂给本组件（与原版手持声呐同一模式），
    /// 服务端只负责解析与属性序列化，所有追踪/渲染逻辑在客户端分部中。
    /// </summary>
    partial class ExploredMapComponent : Powered
    {
        public ExploredMapComponent(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
        }
    }
}
