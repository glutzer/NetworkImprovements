﻿using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

public abstract class PModule
{
    /// <summary>
    /// Config passed in from the behavior in the entity class.
    /// </summary>
    public abstract void Initialize(JsonObject config, Entity entity);

    /// <summary>
    /// Can this be applied this tick?
    /// </summary>
    public abstract bool Applicable(Entity entity, EntityPos pos, EntityControls controls);

    /// <summary>
    /// Apply a modifier to this entity.
    /// </summary>
    public abstract void DoApply(float dt, Entity entity, EntityPos pos, EntityControls controls);
}