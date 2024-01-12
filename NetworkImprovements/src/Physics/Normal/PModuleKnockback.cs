using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

public class PModuleKnockback : PModule
{
    public PModuleKnockback()
    {

    }

    public override void Initialize(JsonObject config, Entity entity)
    {

    }

    public override bool Applicable(Entity entity, EntityPos pos, EntityControls controls)
    {
        return true;
    }

    public override void DoApply(float dt, Entity entity, EntityPos pos, EntityControls controls)
    {
        // If a knockback is queued to happen apply the motion.
        int knockbackState = entity.Attributes.GetInt("dmgkb");
        if (knockbackState == 1)
        {
            double kbX = entity.WatchedAttributes.GetDouble("kbdirX") * 1.5;
            double kbY = entity.WatchedAttributes.GetDouble("kbdirY") * 1.5;
            double kbZ = entity.WatchedAttributes.GetDouble("kbdirZ") * 1.5;

            pos.Motion.X += kbX;
            pos.Motion.Y += kbY;
            pos.Motion.Z += kbZ;

            entity.Attributes.SetInt("dmgkb", 0);
        }
    }
}