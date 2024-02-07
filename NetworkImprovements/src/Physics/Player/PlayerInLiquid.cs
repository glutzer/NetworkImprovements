using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

public class PlayerInLiquid : PModuleInLiquid
{
    public IPlayer player;

    // Stores player attached.
    public PlayerInLiquid(EntityPlayer entityPlayer)
    {
        player = entityPlayer.World.PlayerByUid(entityPlayer.PlayerUID);
    }

    public override void HandleSwimming(float dt, Entity entity, EntityPos pos, EntityControls controls)
    {
        if ((controls.TriesToMove || controls.Jump) && entity.World.ElapsedMilliseconds - lastPush > 2000)
        {
            push = 6f;
            lastPush = entity.World.ElapsedMilliseconds;
            entity.PlayEntitySound("swim", player);
        }
        else
        {
            push = Math.Max(1f, push - (0.1f * dt * 60f));
        }

        Block inBlock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)pos.Y, (int)pos.Z, BlockLayersAccess.Fluid);
        Block aboveBlock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 1), (int)pos.Z, BlockLayersAccess.Fluid);
        Block twoAboveBlock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 2), (int)pos.Z, BlockLayersAccess.Fluid);

        float waterY = (int)pos.Y + (inBlock.LiquidLevel / 8f) + (aboveBlock.IsLiquid() ? 9 / 8f : 0) + (twoAboveBlock.IsLiquid() ? 9 / 8f : 0);
        float bottomSubmergedness = waterY - (float)pos.Y;

        // 0 => at swim line.
        // 1 => completely submerged.
        float swimLineSubmergedness = GameMath.Clamp(bottomSubmergedness - ((float)entity.SwimmingOffsetY), 0, 1);
        swimLineSubmergedness = Math.Min(1, swimLineSubmergedness + 0.075f);

        double yMotion;
        if (controls.Jump)
        {
            yMotion = 0.005f * swimLineSubmergedness * dt * 60;
        }
        else
        {
            yMotion = controls.FlyVector.Y * (1 + push) * 0.03f * swimLineSubmergedness;
        }

        pos.Motion.Add(controls.FlyVector.X * (1 + push) * 0.03f,
                        yMotion,
                        controls.FlyVector.Z * (1 + push) * 0.03f);
    }
}