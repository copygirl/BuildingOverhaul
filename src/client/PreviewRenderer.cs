using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.GameContent;

namespace BuildingOverhaul.Client
{
	public class PreviewRenderer : IRenderer
	{
		public ICoreClientAPI API { get; }
		public RecipeSelectionHandler Selection { get; }
		public ClientMain Game { get; }

		public double RenderOrder => 0.65;
		public int RenderRange => 0;

		public PreviewRenderer(ICoreClientAPI api, RecipeSelectionHandler selection)
		{
			API       = api;
			Selection = selection;
			Game      = (ClientMain)API.GetType().GetField("game", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(API);
			API.Event.RegisterRenderer(this, EnumRenderStage.Opaque);
		}

		public void Dispose() {  }

		private Matrixf _model = new();
		public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
		{
			var recipe = Selection.CurrentRecipe;
			if (recipe == null) return;

			var selection = API.World.Player.CurrentBlockSelection?.Clone();
			var camPos    = API.World.Player.Entity.CameraPos;
			if (selection == null) return;

			var replacedBlock = API.World.BlockAccessor.GetBlock(selection.Position);
			if (!replacedBlock.IsReplacableBy(recipe.Output.Block)) {
				selection.Position.Offset(selection.Face);
				selection.DidOffset = true;
				replacedBlock = API.World.BlockAccessor.GetBlock(selection.Position);
			}

			// Attempt to place this block but capture all SetBlock calls.
			// FIXME: Angled gears cause issues. Create a special handler?
			try {
				var failureCode = "__ignore__";
				BlockAccessorRelaxed_SetBlock_Patch.Skip = true;
				recipe.Output.Block.TryPlaceBlock(API.World, API.World.Player, recipe.Output, selection, ref failureCode);
			} catch {
				// Whatever, LOL!
			} finally {
				BlockAccessorRelaxed_SetBlock_Patch.Skip = false;
			}

			var toRender = new List<RenderInfo>();
			foreach (var entry in BlockAccessorRelaxed_SetBlock_Patch.Captured) {
				var block = API.World.GetBlock(entry.ID);
				if ((block == null) || (block.BlockId == 0)) continue;

				var mesh = API.TesselatorManager.GetDefaultBlockMesh(block);
				if (mesh.IndicesCount == 0) continue;

				var atlasTextureID = InventoryItemRenderer.GetTextureAtlasPosition(
					Game, new ItemStack(block)).atlasTextureId;

				var rotation = 0.0F;
				if (block is BlockGenericTypedContainer) {
					// FIXME: This might not work 100% correctly for selection boxes that have been offset?
					var ePos  = API.World.Player.Entity.Pos;
					var angle = (float)Math.Atan2(ePos.X - (entry.Position.X + selection.HitPosition.X),
					                              ePos.Z - (entry.Position.Z + selection.HitPosition.Z)) * GameMath.RAD2DEG;
					switch (block.Attributes?["rotatatableInterval"]["normal-generic"]?.AsString() ?? "22.5deg") {
						case "22.5deg":
							rotation = (int)Math.Round(angle / 22.5F) * 22.5F + 90.0F;
							break;
						case "22.5degnot45deg":
							var a    = (int)Math.Round(angle / 90.0F) * 90.0F;
							rotation = ((Math.Abs(angle - a) < 22.5F) ? a
								: a + 22.5F * Math.Sign(angle - a)) + 90.0F;
							break;
					}
				}

				toRender.Add(new(mesh, atlasTextureID, entry.Position, rotation));
			}
			BlockAccessorRelaxed_SetBlock_Patch.Captured.Clear();
			if (toRender.Count == 0) return;

			var pos  = selection.Position;
			var prog = API.Render.PreparedStandardShader(pos.X, pos.Y, pos.Z);
			prog.ViewMatrix       = API.Render.CameraMatrixOriginf;
			prog.ProjectionMatrix = API.Render.CurrentProjectionMatrix;

			prog.ExtraGlow  = 128;
			prog.RgbaGlowIn = new(0.5F, 0.75F, 1.0F, 0.5F);
			prog.RgbaTint   = new(1.0F, 1.0F, 1.0F, 1.0F);

			API.Render.GlToggleBlend(true);

			foreach (var renderInfo in toRender) {
				var meshRef = API.Render.UploadMesh(renderInfo.Mesh);
				prog.Tex2D = renderInfo.TextureID;

				pos = renderInfo.Position;
				prog.ModelMatrix = _model
					.Identity()
					.Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z)
					.Translate(0.5F, 0.0F, 0.5F)
					.RotateYDeg(renderInfo.Rotation)
					.Translate(-0.5F, 0.0F, -0.5F)
					.Values;

				API.Render.RenderMesh(meshRef);
				meshRef.Dispose();
			}

			prog.Stop();
		}
	}

	class RenderInfo
	{
		public MeshData Mesh { get; }
		public int TextureID { get; }
		public BlockPos Position { get; }
		public float Rotation { get; }
		public RenderInfo(MeshData mesh, int textureID, BlockPos position, float rotation)
			{ Mesh = mesh; TextureID = textureID; Position = position; Rotation = rotation; }
	}

	[HarmonyPatch(typeof(BlockAccessorRelaxed), "SetBlock")]
	static class BlockAccessorRelaxed_SetBlock_Patch
	{
		public static bool Skip { get; set; } = false;
		public static List<Entry> Captured { get; } = new();

		public static bool Prefix() => !Skip;
		public static void Postfix(ref int blockId, BlockPos pos)
			=> Captured.Add(new(pos, blockId));

		public struct Entry
		{
			public BlockPos Position { get; }
			public int ID { get; }
			public Entry(BlockPos pos, int id) { Position = pos; ID = id; }
		}
	}
}
