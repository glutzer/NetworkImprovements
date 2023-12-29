using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

public class PositionSnapshot
{
    public double x;
    public double y;
    public double z;

    public float yaw;
    public float pitch;
    public float roll;

    public float interval;

    public PositionSnapshot(EntityPos pos, float interval)
    {
        x = pos.X;
        y = pos.Y;
        z = pos.Z;

        yaw = pos.Yaw;
        pitch = pos.Pitch;
        roll = pos.Roll;

        this.interval = interval;
    }

    public void LerpTo(PositionSnapshot snapshot, float delta)
    {
        x = GameMath.Lerp(x, snapshot.x, delta);
        y = GameMath.Lerp(y, snapshot.y, delta);
        z = GameMath.Lerp(z, snapshot.z, delta);
        yaw = GameMath.Lerp(yaw, snapshot.yaw, delta);
        pitch = GameMath.Lerp(pitch, snapshot.pitch, delta);
        roll = GameMath.Lerp(roll, snapshot.roll, delta);
    }
}

public class EntityInterpolation : EntityBehavior, IRenderer
{
    public readonly ICoreClientAPI capi;

    public EntityInterpolation(Entity entity) : base(entity)
    {
        if (entity.World.Side == EnumAppSide.Server) throw new Exception($"Remove server interpolaton behavior from {entity.Code.Path}.");
        capi = entity.Api as ICoreClientAPI;

        //Client player initializes before everything else
        if (capi.World.Player != null && entity != capi.World.Player.Entity)
        {
            
        }

        capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "interpolateposition");
    }

    public PositionSnapshot p0; //Position interpolating from
    public PositionSnapshot p1; //Next position
    public PositionSnapshot p2; //Second next position

    public float accum = 0;
    public long lastReceived = 0;

    public float currentYaw;
    public float targetYaw = 0;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        //Initialize projectile lerps at right hand of the player
        if (entity is EntityProjectile proj && proj.FiredBy != null)
        {
            EntityPos newPos = proj.FiredBy.Pos.Copy();
            
            newPos.Yaw = proj.ServerPos.Yaw;
            newPos.Pitch = proj.ServerPos.Pitch;
            newPos.Roll = proj.ServerPos.Roll;
            newPos.Y += proj.FiredBy.LocalEyePos.Y - 0.3f;

            newPos.X += Math.Sin(newPos.Yaw) * 0.3f;
            newPos.Z += Math.Cos(newPos.Yaw) * 0.3f;

            p2 = new PositionSnapshot(newPos, 0.75f);
            lastReceived = capi.InWorldEllapsedMilliseconds - 100; //Delay for arrow
        }
        else if (entity is EntityThrownStone stone && stone.FiredBy != null)
        {
            EntityPos newPos = stone.FiredBy.Pos.Copy();

            newPos.Yaw = stone.ServerPos.Yaw;
            newPos.Pitch = stone.ServerPos.Pitch;
            newPos.Roll = stone.ServerPos.Roll;
            newPos.Y += stone.FiredBy.LocalEyePos.Y - 0.3f;

            newPos.X += Math.Sin(newPos.Yaw) * 0.3f;
            newPos.Z += Math.Cos(newPos.Yaw) * 0.3f;

            p2 = new PositionSnapshot(newPos, 0.75f);
            lastReceived = capi.InWorldEllapsedMilliseconds - 100; //Delay for stone, since first received pos also contains motion this needs to be backwards calculated instead of a flat 100
        }
        else
        {
            lastReceived = capi.InWorldEllapsedMilliseconds;
        }
        
        currentYaw = entity.ServerPos.Yaw;
        targetYaw = entity.ServerPos.Yaw;
    }

    /// <summary>
    /// Called when the client receives a new position.
    /// Move the positions forward and reset the accumulation.
    /// </summary>
    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        if (p0 != null)
        {
            if (accum < p1.interval)
            {
                float inverseInterval = p1.interval - accum; //How much is left
                float delta = inverseInterval / p1.interval;
                p1.LerpTo(p0, delta); //Lerp to 0 by how much is left
                p2.interval += inverseInterval * 0.99f; //Add time missed with slight compensation

                if (p2.interval > 0.15f) //Fix lingering intervals
                {
                    p2.interval = 0;
                }
            }
            else //accum >= p1.interval
            {
                accum -= p1.interval; //Subtract p1 interval first
                float delta = accum / p2.interval;
                p1.LerpTo(p2, delta); //Lerp p1 (soon to be p0) to p2
            }
        }
        
        p0 = p1;
        p1 = p2;

        float interval = capi.InWorldEllapsedMilliseconds - lastReceived;
        interval /= 1000;
        lastReceived = capi.InWorldEllapsedMilliseconds;

        p2 = new PositionSnapshot(entity.ServerPos, interval);

        accum = 0;

        if (p1 != null) targetYaw = p1.yaw;
    }

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (capi.IsGamePaused) return;

        if (entity == capi.World.Player.Entity) return;

        if (p0 == null)
        {
            entity.Pos.Y = 500;
            return;
        }

        accum += dt;

        //Don't interpolate mount if the player is controlling it because player controlled mounts are done client-side
        bool isMounted = entity is EntityAgent { MountedOn: not null };
        if (entity is IMountableSupplier mount)
        {
            foreach (IMountable seat in mount.MountPoints)
            {
                if (seat.MountedBy == capi.World.Player.Entity && seat.CanControl)
                {
                    return;
                }
            }
        }

        //If the entity is mounted on something
        if (accum < p1.interval)
        {
            float delta = accum / p1.interval;

            if (!isMounted)
            {
                entity.Pos.X = GameMath.Lerp(p0.x, p1.x, delta);
                entity.Pos.Y = GameMath.Lerp(p0.y, p1.y, delta);
                entity.Pos.Z = GameMath.Lerp(p0.z, p1.z, delta);
            }

            entity.Pos.Pitch = GameMath.Lerp(p0.pitch, p1.pitch, delta);
            entity.Pos.Roll = GameMath.Lerp(p0.roll, p1.roll, delta);
        }
        else if (accum < p2.interval)
        {
            float delta = (accum - p1.interval) / p2.interval;

            if (!isMounted)
            {
                entity.Pos.X = GameMath.Lerp(p1.x, p2.x, delta);
                entity.Pos.Y = GameMath.Lerp(p1.y, p2.y, delta);
                entity.Pos.Z = GameMath.Lerp(p1.z, p2.z, delta);
            }

            entity.Pos.Pitch = GameMath.Lerp(p1.pitch, p2.pitch, delta);
            entity.Pos.Roll = GameMath.Lerp(p1.roll, p2.roll, delta);
        }

        //Lerp the old way for yaw
        double percentYawDiff = Math.Abs(GameMath.AngleRadDistance(currentYaw, targetYaw)) * dt / 0.1f;
        int signY = Math.Sign(percentYawDiff);
        currentYaw += 0.6f * (float)GameMath.Clamp(GameMath.AngleRadDistance(currentYaw, targetYaw), -signY * percentYawDiff, signY * percentYawDiff);
        currentYaw %= GameMath.TWOPI;
        entity.Pos.Yaw = currentYaw;

        //Entity agent rotations need to be lerped here too, I didn't finish this
        if (entity is EntityAgent entityAgent)
        {
            double percentBodyYawDiff = Math.Abs(GameMath.AngleRadDistance(entityAgent.BodyYaw, entityAgent.BodyYawServer)) * dt / 0.1f;
            int signBY = Math.Sign(percentBodyYawDiff);
            entityAgent.BodyYaw += 0.6f * (float)GameMath.Clamp(GameMath.AngleRadDistance(entityAgent.BodyYaw, entityAgent.BodyYawServer), -signBY * percentBodyYawDiff, signBY * percentBodyYawDiff);
            entityAgent.BodyYaw %= GameMath.TWOPI;
        }
    }

    public override string PropertyName()
    {
        return "entityinterpolation";
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Before);
    }

    public void Dispose()
    {

    }

    public double RenderOrder => 0;
    public int RenderRange => 9999;
}