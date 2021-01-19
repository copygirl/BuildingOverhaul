using System.Collections.Generic;
using System.Reflection.Emit;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using HarmonyLib;

[assembly: ModInfo("BuildingOverhaul",
	Description = "",
	Website = "",
	Authors = new []{ "copygirl" })]

namespace BuildingOverhaul
{
	public class BuildingOverhaulSystem : ModSystem
	{
		public const string MOD_ID = "buildingoverhaul";

		public const string FAILURE_NO_HAMMER    = MOD_ID + "-nohammer";
		public const string FAILURE_NO_RECIPE    = MOD_ID + "-norecipe";
		public const string FAILURE_NO_MATERIALS = MOD_ID + "-nomaterials";

		// Client
		public static BuildingOverhaulSystem Instance { get; private set; }
		public ICoreClientAPI ClientAPI { get; private set; }
		public IClientNetworkChannel ClientChannel { get; private set; }
		public Harmony Harmony { get; private set; }

		// Server
		public ICoreServerAPI ServerAPI { get; private set; }
		public IServerNetworkChannel ServerChannel { get; private set; }

		public override void StartClientSide(ICoreClientAPI api)
		{
			Instance  = this;
			ClientAPI = api;

			ClientChannel = api.Network.RegisterChannel(MOD_ID);
			ClientChannel.RegisterMessageType<BuildingMessage>();

			Harmony = new(MOD_ID);
			Harmony.PatchAll();
		}

		public override void Dispose()
		{
			if (ClientAPI != null) {
				Instance = null;
				Harmony.UnpatchAll(MOD_ID);
			}
		}

		public override void StartServerSide(ICoreServerAPI api)
		{
			ServerChannel = api.Network.RegisterChannel(MOD_ID);
			ServerChannel.RegisterMessageType<BuildingMessage>();
			ServerChannel.SetMessageHandler<BuildingMessage>(OnBuildingMessage);
		}

		public bool OnInWorldInteract()
		{
			var player    = ClientAPI.World?.Player;
			var selection = player?.CurrentBlockSelection?.Clone();
			if (selection == null) return false;

			var result = TryBuild(player, selection, true);
			if (!result.IsSuccess) {
				// Ignore missing hammer and missing recipe failures,
				// this just means we aren't holding the right items.
				if ((result.FailureCode == FAILURE_NO_HAMMER) ||
				    (result.FailureCode == FAILURE_NO_RECIPE)) return false;
				// But otherwise do show an error message.
				ClientAPI.TriggerIngameError(this, result.FailureCode,
					Lang.Get("placefailure-" + result.FailureCode, result.LangParams));
			} else ClientChannel.SendPacket(new BuildingMessage(selection));
			return true;
		}

		private void OnBuildingMessage(IServerPlayer player, BuildingMessage message)
		{
			var result = TryBuild(player, message.Selection, false);
			if (!result.IsSuccess) {
				player.SendIngameError(result.FailureCode, null, result.LangParams);
				player.Entity.World.BlockAccessor.MarkBlockDirty(message.Selection.Position);
			}
		}

		private BuildResult TryBuild(IPlayer player, BlockSelection selection/*, shape? */, bool doOffset)
		{
			// TODO: Required tool shouldn't have to be a hammer.
			var inventory = player.InventoryManager;
			var offhandItem = inventory.GetOwnInventory(GlobalConstants.hotBarInvClassName)[10].Itemstack;
			if (!(offhandItem.Item is ItemHammer))
				return new(){ FailureCode = FAILURE_NO_HAMMER };
			var hotbarItem = inventory.ActiveHotbarSlot.Itemstack;
			if ((hotbarItem == null) || !hotbarItem.Collectible.Code.BeginsWith("game", "plank-"))
				return new(){ FailureCode = FAILURE_NO_RECIPE };

			var world    = player.Entity.World;
			var newBlock = world.GetBlock(new AssetLocation("game", $"planks-{hotbarItem.Collectible.LastCodePart()}"));

			if (doOffset) {
				var clickedBlock = world.BlockAccessor.GetBlock(selection.Position);
				if (!clickedBlock.IsReplacableBy(newBlock)) {
					selection.Position.Offset(selection.Face);
					selection.DidOffset = true;
				}
			}

			var failureCode = "__ignore__";
			return newBlock.TryPlaceBlock(world, player, new(newBlock), selection, ref failureCode)
				? new() : new(){ FailureCode = failureCode };
		}

		private class BuildResult
		{
			public string FailureCode  = "__ignore__";
			public object[] LangParams = new object[0];

			public bool IsSuccess => (FailureCode == "__ignore__");
		}

		[HarmonyPatch(typeof(SystemMouseInWorldInteractions), "HandleMouseInteractionsBlockSelected")]
		class SystemMouseInWorldInteractions_HandleMouseInteractionsBlockSelected_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(
				IEnumerable<CodeInstruction> instructions,
				ILGenerator generator)
			{
				var enumerator = instructions.GetEnumerator();

				// Yield instructions until this specific one:
				//   var handling = EnumHandling.PassThrough;
				var HANDLING_INDEX = 7; // Index of the handling method local variable.
				while (enumerator.MoveNext()) {
					yield return enumerator.Current;
					if ((enumerator.Current.opcode == OpCodes.Stloc_S) &&
					    (enumerator.Current.operand is LocalBuilder local) &&
					    (local.LocalIndex == HANDLING_INDEX)) break;
				}

				// Insert call to BuildingOverhaulSystem.Instance.OnInWorldInteract.
				// If OnInWorldInteract returns true, return from the method immediately.
				var falseLabel = generator.DefineLabel();
				yield return new(OpCodes.Call, typeof(BuildingOverhaulSystem).GetProperty(nameof(BuildingOverhaulSystem.Instance)).GetMethod);
				yield return new(OpCodes.Callvirt, typeof(BuildingOverhaulSystem).GetMethod(nameof(BuildingOverhaulSystem.OnInWorldInteract)));
				yield return new(OpCodes.Brfalse_S, falseLabel);
				yield return new(OpCodes.Ret);

				enumerator.MoveNext();
				yield return new(enumerator.Current){ labels = new(){ falseLabel } };

				// Yield the rest of the instructions.
				while (enumerator.MoveNext())
					yield return enumerator.Current;
			}
		}
	}
}
