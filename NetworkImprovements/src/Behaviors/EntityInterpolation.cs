using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

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
    public ICoreClientAPI capi;
    public EntityAgent agent;

    public EntityInterpolation(Entity entity) : base(entity)
    {
        if (entity.World.Side == EnumAppSide.Server) throw new Exception($"Remove server interpolation behavior from {entity.Code.Path}.");

        capi = entity.Api as ICoreClientAPI;

        capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "interpolateposition");

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

    public void PushQueue(PositionSnapshot snapshot)
    {
        positionQueue.Enqueue(snapshot);
        queueCount++;
    }

    // Interval at what things should be received.
    public float interval = 1 / 15f;
    public int queueCount;

    public void PopQueue(bool clear)
    {
        // Lerping only starts if pL is not null.
        dtAccum -= pN.interval;

        if (dtAccum < 0) dtAccum = 0;
        if (dtAccum > 1) dtAccum = 0;

        pL = pN;
        pN = positionQueue.Dequeue();
        queueCount--;

        // Clear flooded queue.
        if (clear)
        {
            if (queueCount > 1) PopQueue(true);
        }
    }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        currentYaw = entity.ServerPos.Yaw;
        targetYaw = entity.ServerPos.Yaw;

        PushQueue(new PositionSnapshot(entity.ServerPos, 0));

        targetYaw = entity.ServerPos.Yaw;
        targetPitch = entity.ServerPos.Pitch;
        targetRoll = entity.ServerPos.Roll;

        currentYaw = entity.ServerPos.Yaw;
        currentPitch = entity.ServerPos.Pitch;
        currentRoll = entity.ServerPos.Roll;

        if (agent != null)
        {
            targetHeadYaw = entity.ServerPos.HeadYaw;
            targetHeadPitch = entity.ServerPos.HeadPitch;
            targetBodyYaw = agent.BodyYawServer;

            currentHeadYaw = entity.ServerPos.HeadYaw;
            currentHeadPitch = entity.ServerPos.HeadPitch;
            currentBodyYaw = agent.BodyYawServer;
        }
    }

    /// <summary>
    /// Called when the client receives a new position.
    /// Move the positions forward and reset the accumulation.
    /// </summary>
    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        PushQueue(new PositionSnapshot(entity.ServerPos, entity.WatchedAttributes.GetBool("lr") ? interval * 5 : interval));

        if (isTeleport)
        {
            dtAccum = 0;
            positionQueue.Clear();
            queueCount = 0;

            PushQueue(new PositionSnapshot(entity.ServerPos, entity.WatchedAttributes.GetBool("lr") ? interval * 5 : interval));
            PushQueue(new PositionSnapshot(entity.ServerPos, entity.WatchedAttributes.GetBool("lr") ? interval * 5 : interval));

            PopQueue(false);
            PopQueue(false);
        }

        targetYaw = entity.ServerPos.Yaw;
        targetPitch = entity.ServerPos.Pitch;
        targetRoll = entity.ServerPos.Roll;

        if (agent != null)
        {
            targetHeadYaw = entity.ServerPos.HeadYaw;
            targetHeadPitch = entity.ServerPos.HeadPitch;
            targetBodyYaw = agent.BodyYawServer;
        }
    }

    public int wait = 0;

    public float targetSpeed = 0.7f;

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

        entity.Pos.Yaw = LerpRotation(ref currentYaw, targetYaw, dt);
        entity.Pos.Pitch = LerpRotation(ref currentPitch, targetPitch, dt);
        entity.Pos.Roll = LerpRotation(ref currentRoll, targetRoll, dt);

        if (agent != null)
        {
            entity.Pos.HeadYaw = LerpRotation(ref currentHeadYaw, targetHeadYaw, dt);
            entity.Pos.HeadPitch = LerpRotation(ref currentHeadPitch, targetHeadPitch, dt);
            agent.BodyYaw = LerpRotation(ref currentBodyYaw, targetBodyYaw, dt);
        }

        if (queueCount < wait)
        {
            return;
        }

        dtAccum += dt * targetSpeed;

        // If over the interval and there's no queues stop the entity until it can be re-synced.
        while (dtAccum > pN.interval)
        {
            if (queueCount > 0)
            {
                PopQueue(false);
                wait = 0;
            }
            else
            {
                wait = 1;
                break;
            }
        }

        if (queueCount > 20)
        {
            PopQueue(true);
        }

        float speed = (queueCount * 0.2f) + 0.8f;
        targetSpeed = GameMath.Lerp(targetSpeed, speed, dt * 4);

        // If the entity is an agent and mounted on something.
        bool isMounted = entity is EntityAgent { MountedOn: not null };

        // Set controlling seat to the position of the controlling player here.
        if (entity is IMountableSupplier mount)
        {
            foreach (IMountable seat in mount.MountPoints)
            {
                if (seat.MountedBy == capi.World.Player.Entity)
                {
                    //return;
                }
                else
                {
                    seat.MountedBy?.Pos.SetFrom(seat.MountPosition);
                }
            }
        }

        float delta = dtAccum / pN.interval;
        if (wait != 0) delta = 1; // So FPS below 15 can function.

        // Only lerp position if not mounted.
        if (!isMounted)
        {
            entity.Pos.X = GameMath.Lerp(pL.x, pN.x, delta);
            entity.Pos.Y = GameMath.Lerp(pL.y, pN.y, delta);
            entity.Pos.Z = GameMath.Lerp(pL.z, pN.z, delta);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LerpRotation(ref float current, float target, float dt)
    {
        float pDiff = Math.Abs(GameMath.AngleRadDistance(current, target)) * dt / 0.1f;
        int signY = Math.Sign(pDiff);
        current += 0.6f * Math.Clamp(GameMath.AngleRadDistance(current, target), -signY * pDiff, signY * pDiff);
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