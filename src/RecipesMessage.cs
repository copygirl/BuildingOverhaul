using System.Collections.Generic;
using System.IO;
using ProtoBuf;

namespace BuildingOverhaul
{
	[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
	public class RecipesMessage
	{
		private byte[] _data;

		private RecipesMessage() {  }

		public RecipesMessage(List<List<BuildingRecipe>> recipesByTool)
		{
			var ms = new MemoryStream();
			using var writer = new BinaryWriter(ms);
			writer.Write(recipesByTool.Count);
			foreach (var recipes in recipesByTool) {
				writer.Write(recipes.Count);
				foreach (var recipe in recipes)
					recipe.ToBytes(writer);
			}
			_data = ms.ToArray();
		}

		public List<List<BuildingRecipe>> UnpackRecipes()
		{
			using var reader = new BinaryReader(new MemoryStream(_data));
			var recipesByTool = new List<List<BuildingRecipe>>(reader.ReadInt32());
			for (var i = 0; i < recipesByTool.Capacity; i++) {
				var recipes = new List<BuildingRecipe>(reader.ReadInt32());
				for (var j = 0; j < recipes.Capacity; j++) {
					var recipe = new BuildingRecipe();
					recipe.FromBytes(reader, null);
					recipes.Add(recipe);
				}
				recipesByTool.Add(recipes);
			}
			return recipesByTool;
		}
	}
}
