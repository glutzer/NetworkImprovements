﻿using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

public class PModuleWind : PModule
{
    public Vec3d windForce = new();
    public bool applyWindForce = false;
    public float accum = 0;

    public override void Initialize(JsonObject config, Entity entity)
    {
        applyWindForce = entity.World.Config.GetBool("windAffectedEntityMovement", false);
    }

    public override bool Applicable(Entity entity, EntityPos pos, EntityControls controls)
    {
        // Applies if trying to walk on the surface.
        return controls.TriesToMove && entity.OnGround && !entity.Swimming;
    }

    public override void DoApply(float dt, Entity entity, EntityPos pos, EntityControls controls)
    {
        // Update wind force every second and apply the motion to the entity.
        accum += dt;
        if (accum > 5)
        {
            accum = 0;
            UpdateWindForce(entity);
        }
        pos.Motion.Add(windForce);
    }

    public virtual void UpdateWindForce(Entity entity)
    {
        if (!entity.Alive || !applyWindForce)
        {
            windForce.Set(0, 0, 0);
            return;
        }

        int rainY = entity.World.BlockAccessor.GetRainMapHeightAt((int)entity.Pos.X, (int)entity.Pos.Z);
        if (rainY > entity.Pos.Y)
        {
            windForce.Set(0, 0, 0);
            return;
        }

        Vec3d windSpeed = entity.World.BlockAccessor.GetWindSpeedAt(entity.Pos.XYZ);
        windForce.X = Math.Max(0, Math.Abs(windSpeed.X) - 0.8) / 40f * Math.Sign(windSpeed.X);
        windForce.Y = Math.Max(0, Math.Abs(windSpeed.Y) - 0.8) / 40f * Math.Sign(windSpeed.Y);
        windForce.Z = Math.Max(0, Math.Abs(windSpeed.Z) - 0.8) / 40f * Math.Sign(windSpeed.Z);
    }
}