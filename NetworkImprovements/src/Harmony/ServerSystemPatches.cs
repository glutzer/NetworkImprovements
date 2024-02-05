using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

// COMBAT FIX.

// REMOVE ATTRIBUTE SENDING IN INTERVAL.
public class ServerSystemPatches
{
    // Increase range at which hits are allowed, also increase valid picking range.
    // Minecraft also does this for lag compensation which is why kill aura can hit people farther away. The only other solution would be to keep a log of previous player locations and also test those.
    // Counter strike also does that.
    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "HandleEntityInteraction")]
    public static class CombatPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ServerSystemEntitySimulation __instance, Packet_Client packet, ConnectedClient client)
        {
            ServerPlayer player = client.Player;
            if (player.WorldData.CurrentGameMode == EnumGameMode.Spectator) return false;

            Packet_EntityInteraction p = packet.EntityInteraction;
            Entity[] entitiesAround = __instance.GetField<ServerMain>("server").GetEntitiesAround(player.Entity.ServerPos.XYZ, player.WorldData.PickingRange + 10f, player.WorldData.PickingRange + 10f, (Entity e) => e.EntityId == p.EntityId);
            if (entitiesAround == null || entitiesAround.Length == 0)
            {
                ServerMain.Logger.Debug("HandleEntityInteraction received from client " + client.PlayerName + " but no such entity found in his range");
                return false;
            }

            Entity entity = entitiesAround[0];
            Cuboidd cuboidd = entity.SelectionBox.ToDouble().Translate(entity.SidedPos.X, entity.SidedPos.Y, entity.SidedPos.Z);
            EntityPos sidedPos = client.Entityplayer.SidedPos;
            ItemStack itemStack = client.Player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            float range = itemStack?.Collectible.GetAttackRange(itemStack) ?? GlobalConstants.DefaultAttackRange;
            if ((cuboidd.ShortestDistanceFrom(sidedPos.X + client.Entityplayer.LocalEyePos.X, sidedPos.Y + client.Entityplayer.LocalEyePos.Y, sidedPos.Z + client.Entityplayer.LocalEyePos.Z) > (double)(range * 2) && p.MouseButton == 0) || (p.MouseButton == 0 && (((!__instance.GetField<ServerMain>("server").Config.AllowPvP || !player.HasPrivilege("attackplayers")) && entity is EntityPlayer) || (!player.HasPrivilege("attackcreatures") && entity is EntityAgent))))
            {
                return false;
            }

            EntityPlayer entityPlayer = entity as EntityPlayer;
            if (entityPlayer != null)
            {
                if (entityPlayer.Player is not IServerPlayer obj || obj.ConnectionState != EnumClientState.Playing)
                {
                    return false;
                }
            }

            Vec3d hitPosition = new(CollectibleNet.DeserializeDouble(p.HitX), CollectibleNet.DeserializeDouble(p.HitY), CollectibleNet.DeserializeDouble(p.HitZ));
            EnumHandling handling = EnumHandling.PassThrough;
            __instance.GetField<ServerMain>("server").EventManager.TriggerPlayerInteractEntity(entity, player, player.GetField<PlayerInventoryManager>("inventoryMgr").ActiveHotbarSlot, hitPosition, p.MouseButton, ref handling);
            if (handling == EnumHandling.PassThrough)
            {
                entity.OnInteract(player.Entity, player.InventoryManager.ActiveHotbarSlot, hitPosition, (p.MouseButton != 0) ? EnumInteractMode.Interact : EnumInteractMode.Attack);
            }
            return false;
        }
    }

    // Send entity attribute methods after physics ticks in physics manager instead. Default 15/s.
    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "UpdateEvery200ms")]
    public static class RemoveAttribUpdatesPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ServerSystemEntitySimulation __instance)
        {
            __instance.CallMethod("VerifyPlayerPositions");
            return false;
        }
    }
}