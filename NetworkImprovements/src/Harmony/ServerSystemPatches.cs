using HarmonyLib;
using ProperVersion;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

public class ServerSystemPatches
{
    // Increase range of allowed hits.
    // Alternative solution: log position of entity 1 second ago and also check that.
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
                ServerMain.Logger.Debug("HandleEntityInteraction received from client " + client.PlayerName + " but no such entity found in his range!");
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

    // No longer send entity attributes here. Verify player position also not checked because that's done elsewhere now.
    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "UpdateEvery200ms")]
    public static class RemoveAttribUpdatesPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ServerSystemEntitySimulation __instance)
        {
            //__instance.CallMethod("VerifyPlayerPositions"); Not needed now, done properly.
            return false;
        }
    }

    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "OnServerTick")]
    public static class SendSpawnsPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ServerSystemEntitySimulation __instance, float dt)
        {
            ServerMain server = __instance.GetField<ServerMain>("server");

            foreach (ConnectedClient client in server.Clients.Values)
            {
                if (client.IsPlayingClient)
                {
                    ServerPlayer player = client.Player;
                    player.Entity.PreviousBlockSelection = player.Entity.BlockSelection?.Position.Copy();
                    bool bFilter(BlockPos pos, Block block) => block == null || block.RenderPass != EnumChunkRenderPass.Meta || client.WorldData.RenderMetaBlocks;
                    bool eFilter(Entity e) => e.IsInteractable && e.EntityId != player.Entity.EntityId;
                    server.RayTraceForSelection(player, ref player.Entity.BlockSelection, ref player.Entity.EntitySelection, bFilter, eFilter);
                    if (player.Entity.BlockSelection != null)
                    {
                        bool firstTick = player.Entity.PreviousBlockSelection == null || player.Entity.BlockSelection.Position != player.Entity.PreviousBlockSelection;
                        server.BlockAccessor.GetBlock(player.Entity.BlockSelection.Position).OnBeingLookedAt(player, player.Entity.BlockSelection, firstTick);
                    }
                }
            }

            __instance.CallMethod("TickEntities", dt);

            __instance.CallMethod("endPlayerEntityDeaths");

            //SendEntitySpawns(); // Done in physics manager now.

            __instance.SetField("accum", __instance.GetField<float>("accum") + dt);
            if (__instance.GetField<float>("accum") > 3f)
            {
                __instance.CallMethod("UpdateEntitiesTickingFlag");
            }

            return false;
        }
    }
}