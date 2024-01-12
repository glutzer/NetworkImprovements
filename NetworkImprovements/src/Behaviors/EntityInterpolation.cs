using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

public struct PositionSnapshot
{
    public double x;
    public double y;
    public double z;

    public float interval;

    public PositionSnapshot(EntityPos pos, float interval)
    {
        x = pos.X;
        y = pos.Y;
        z = pos.Z;

        this.interval = interval;
    }

    public PositionSnapshot(double x, double y, double z, float interval)
    {
        this.x = x;
        this.y = y;
        this.z = z;

        this.interval = interval;
    }

    public readonly PositionSnapshot Clone()
    {
        return new PositionSnapshot(x, y, z, interval);
    }
}

public class EntityInterpolation : EntityBehavior, IRenderer
{
    public readonly ICoreClientAPI capi;
    public bool item = false;
    public EntityAgent agent;

    public EntityInterpolation(Entity entity) : base(entity)
    {
        if (entity.World.Side == EnumAppSide.Server) throw new Exception($"Remove server interpolation behavior from {entity.Code.Path}.");
        capi = entity.Api as ICoreClientAPI;

        capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "interpolateposition");

        item = entity is EntityItem;
        agent = entity as EntityAgent;
    }

    public float dtAccum = 0;

    // Will lerp from pL to pN.
    public PositionSnapshot pL;
    public PositionSnapshot pN;

    public Queue<PositionSnapshot> positionQueue = new();

    public float currentYaw;
    public float targetYaw;

    public float currentPitch;
    public float targetPitch;

    public float currentRoll;
    public float targetRoll;

    public float currentHeadYaw;
    public float targetHeadYaw;

    public float currentHeadPitch;
    public float targetHeadPitch;

    public float currentBodyYaw;
    public float targetBodyYaw;

    public bool awaitQueue = true;

    public void PushQueue(PositionSnapshot snapshot)
    {
        positionQueue.Enqueue(snapshot);
    }

    // Interval at what things should be received.
    public float interval = 1 / 15f;

    public void PopQueue()
    {
        // Lerping only starts if pL is not null.
        if (!awaitQueue)
        {
            dtAccum -= pN.interval;

            if (dtAccum < 0) dtAccum = 0;
        }

        if (dtAccum > 1) dtAccum = 0;

        pL = pN;
        pN = positionQueue.Dequeue();

        // Clear flooded queue.
        while (positionQueue.Count > 1) PopQueue();
    }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        currentYaw = entity.ServerPos.Yaw;
        targetYaw = entity.ServerPos.Yaw;

        lastIdle.Set(entity.ServerPos.X, 1000, entity.ServerPos.Z);

        PushQueue(new PositionSnapshot(entity.ServerPos, 0));

        targetYaw = entity.ServerPos.Yaw;
        targetPitch = entity.ServerPos.Pitch;
        targetRoll = entity.ServerPos.Roll;

        targetHeadYaw = entity.ServerPos.HeadYaw;
        targetHeadPitch = entity.ServerPos.HeadPitch;

        currentYaw = entity.ServerPos.Yaw;
        currentPitch = entity.ServerPos.Pitch;
        currentRoll = entity.ServerPos.Roll;

        currentHeadYaw = entity.ServerPos.HeadYaw;
        currentHeadPitch = entity.ServerPos.HeadPitch;
    }

    public Action OnFirstReceived;
    public float lastMalus;

    /// <summary>
    /// Called when the client receives a new position.
    /// Move the positions forward and reset the accumulation.
    /// </summary>
    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        PushQueue(new PositionSnapshot(entity.ServerPos, interval));

        targetYaw = entity.ServerPos.Yaw;
        targetPitch = entity.ServerPos.Pitch;
        targetRoll = entity.ServerPos.Roll;

        targetHeadYaw = entity.ServerPos.HeadYaw;
        targetHeadPitch = entity.ServerPos.HeadPitch;

        if (agent != null)
        {
            targetBodyYaw = agent.BodyYawServer;
        }

        OnFirstReceived?.Invoke();

        if (isTeleport)
        {
            lastIdle.Set(pN.x, pN.y, pN.z);
            dtAccum = 0;

            awaitQueue = true;
        }
    }

    public Vec3d lastIdle = new();

    // This can be a problem if there's a thousand item entities on the ground?
    // If queue is empty do nothing and return.
    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (capi.IsGamePaused) return;

        if (entity == capi.World.Player.Entity)
        {
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Before);
            return;
        }

        if (awaitQueue)
        {
            if (positionQueue.Count > 2)
            {
                awaitQueue = false;
                PopQueue();
            }
            else
            {
                entity.Pos.SetFrom(lastIdle);
                return;
            }
        }

        dtAccum += dt;

        // If over the interval and there's no queues stop the entity until it can be re-synced.
        while (dtAccum > pN.interval)
        {
            if (positionQueue.Count > 0)
            {
                PopQueue();
            }
            else
            {
                lastIdle.Set(pN.x, pN.y, pN.z);
                dtAccum = 0;

                awaitQueue = true;
                return;
            }
        }

        if (positionQueue.Count > 1)
        {
            PopQueue();
        }

        // If the entity is an agent and mounted on something.
        bool isMounted = entity is EntityAgent { MountedOn: not null };

        // Set controlling seat to the position of the controlling player here.
        if (entity is IMountableSupplier mount)
        {
            foreach (IMountable seat in mount.MountPoints)
            {
                if (seat.MountedBy == capi.World.Player.Entity)
                {
                    return;
                }
                else
                {
                    seat.MountedBy?.Pos.SetFrom(seat.MountPosition);
                }
            }
        }

        float delta = dtAccum / pN.interval;

        // Only lerp position if not mounted.
        if (!isMounted)
        {
            entity.Pos.X = GameMath.Lerp(pL.x, pN.x, delta);
            entity.Pos.Y = GameMath.Lerp(pL.y, pN.y, delta);
            entity.Pos.Z = GameMath.Lerp(pL.z, pN.z, delta);
        }

        entity.Pos.Yaw = LerpRotation(ref currentYaw, targetYaw, dt);
        entity.Pos.Pitch = LerpRotation(ref currentPitch, targetPitch, dt);
        entity.Pos.Roll = LerpRotation(ref currentRoll, targetRoll, dt);

        if (!item)
        {
            entity.Pos.HeadYaw = LerpRotation(ref currentHeadYaw, targetHeadYaw, dt);
            entity.Pos.HeadPitch = LerpRotation(ref currentHeadPitch, targetHeadPitch, dt);

            if (agent != null)
            {
                double percentBodyYawDiff = Math.Abs(GameMath.AngleRadDistance(agent.BodyYaw, agent.BodyYawServer)) * dt / 0.1f;
                int signY = Math.Sign(percentBodyYawDiff);
                agent.BodyYaw += 0.6f * (float)GameMath.Clamp(GameMath.AngleRadDistance(agent.BodyYaw, agent.BodyYawServer), -signY * percentBodyYawDiff, signY * percentBodyYawDiff);
                agent.BodyYaw %= GameMath.TWOPI;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LerpRotation(ref float current, float target, float dt)
    {
        float pDiff = Math.Abs(GameMath.AngleRadDistance(current, target)) * dt / 0.1f;
        int signY = Math.Sign(pDiff);
        current += 0.7f * Math.Clamp(GameMath.AngleRadDistance(current, target), -signY * pDiff, signY * pDiff);
        current %= GameMath.TWOPI;
        return current;
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