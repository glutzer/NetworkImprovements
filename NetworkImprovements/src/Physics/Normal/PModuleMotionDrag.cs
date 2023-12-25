using Vintagestory.API.Common.Entities;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using System;

public class PModuleMotionDrag : PModule
{
    public double waterDragValue = GlobalConstants.WaterDrag;
    public double airDragValue = GlobalConstants.AirDragAlways;

    public override void Initialize(JsonObject config, Entity entity)
    {
        if (config != null)
        {
            waterDragValue = 1 - (1 - GlobalConstants.WaterDrag) * (float)config["waterDragFactor"].AsDouble(1);
            airDragValue = 1 - (1 - GlobalConstants.AirDragAlways) * (float)config["airDragFallingFactor"].AsDouble(1);
        }
    }

    /// <summary>
    /// Motion drag applied to every entity.
    /// </summary>
    public override bool Applicable(Entity entity, EntityPos pos, EntityControls controls)
    {
        return true;
    }

    public override void DoApply(float dt, Entity entity, EntityPos pos, EntityControls controls)
    {
        //In the water, multiply by water drag value
        //Why power?
        if (entity.FeetInLiquid || entity.Swimming)
        {
            pos.Motion *= (float)Math.Pow(waterDragValue, dt * 33);
        }
        else //Air drag value on ground and air
        {
            pos.Motion *= (float)Math.Pow(airDragValue, dt * 33);
        }

        //If you're flying and not gliding (creative) apply air drag that slows you down gently
        if (controls.IsFlying && !controls.Gliding)
        {
            pos.Motion *= (float)Math.Pow(GlobalConstants.AirDragFlying, dt * 33);
        }
    }
}