//! The kinds of inventory request the client sends and waits on.

/// What an inventory request asked for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum InventoryRequestKind {
    /// Pick an item up from the world.
    Pickup,
    /// Put an item in a container.
    PutInContainer,
    /// Split part of a stack into a container.
    SplitToContainer,
    /// Merge one stack into another.
    Merge,
    /// Move an item between containers.
    Move,
    /// Drop an item on the ground.
    DropToWorld,
    /// Split part of a stack onto the ground.
    SplitToWorld,
    /// Wield an item.
    Wield,
    /// Give an item to someone.
    Give,
    /// Buy or sell at a vendor.
    Shop,
}
