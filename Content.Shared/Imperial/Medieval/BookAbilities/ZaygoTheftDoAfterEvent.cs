using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared.Imperial.Medieval.BookAbilities;

[Serializable, NetSerializable]
public sealed partial class ZaygoTheftDoAfterEvent : DoAfterEvent
{
    public NetEntity Item;
    public NetEntity Bag;
    public NetEntity Root;
    public override DoAfterEvent Clone() => (ZaygoTheftDoAfterEvent) MemberwiseClone();
}
