using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

public class PModuleOnGround : PModule
{
    public long lastJump; //Time the player last jumped

    public double groundDragFactor = 0.3f; //Factor motion will be slowed by the ground

    public float accum;

    public float coyoteTimer; //Time the player can walk off an edge before gravity applies

    public Vec3d motionDelta = new();

    public override void Initialize(JsonObject config, Entity entity)
    {
        if (config != null) groundDragFactor = 0.3 * (float)config["groundDragFactor"].AsDouble(1);
    }

    public override bool Applicable(Entity entity, EntityPos pos, EntityControls controls)
    {
        bool onGround = entity.OnGround && !entity.Swimming;

        if (onGround) coyoteTimer = 0.15f;

        return onGround || coyoteTimer > 0;
    }

    /// <summary>
    /// Applies every physics tick (50ms)
    /// </summary>
    public override void DoApply(float dt, Entity entity, EntityPos pos, EntityControls controls)
    {
        coyoteTimer -= dt; //Tick coyote time

        Block belowBlock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y - 0.05f), (int)pos.Z); //Get block below the player

        //Physics are applied at fixed ticks now
        //But accumulator for this stays since it's only here

        accum = Math.Min(1, accum + dt);
        float frameTime = 1 / 60f;

        while (accum > frameTime)
        {
            accum -= frameTime;

            if (entity.Alive)
            {
                // pply walk motion
                double multiplier = (entity as EntityAgent).GetWalkSpeedMultiplier(groundDragFactor);

                motionDelta.Set(    motionDelta.X + (controls.WalkVector.X * multiplier - motionDelta.X) * belowBlock.DragMultiplier,
                                    0,
                                    motionDelta.Z + (controls.WalkVector.Z * multiplier - motionDelta.Z) * belowBlock.DragMultiplier);

                pos.Motion.Add(motionDelta.X, 0, motionDelta.Z);
            }

            //Apply ground drag
            double dragStrength = 1 - groundDragFactor;

            pos.Motion.X *= dragStrength;
            pos.Motion.Z *= dragStrength;
        }

        //Only able to jump every 500ms. Only works while on the ground
        if (controls.Jump && entity.World.ElapsedMilliseconds - lastJump > 500 && entity.Alive)
        {
            lastJump = entity.World.ElapsedMilliseconds;

            //Set jump motion to something
            pos.Motion.Y = GlobalConstants.BaseJumpForce * 1 / 60f;

            //Play jump sound
            EntityPlayer entityPlayer = entity as EntityPlayer;
            IPlayer player = entityPlayer?.World.PlayerByUid(entityPlayer.PlayerUID);
            entity.PlayEntitySound("jump", player, false);
        }
    }
}