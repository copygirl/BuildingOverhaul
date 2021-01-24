using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace BuildingOverhaul
{
	public class Recipe
	{
		[JsonIgnore]
		public AssetLocation Location { get; set; }

		public bool Enabled { get; set; } = true;
		public string Shape { get; set; }


		[JsonConverter(typeof(IngredientConverter), EnumItemClass.Item, true, new []{ "quantity" })]
		public Ingredient Tool { get; set; }

		[JsonConverter(typeof(IngredientConverter), EnumItemClass.Item, true, new []{ "quantity" })]
		public Ingredient Material { get; set; }

		[JsonProperty(ItemConverterType = typeof(IngredientConverter),
		              ItemConverterParameters = new object[]{ EnumItemClass.Item, true, new []{ "name" } })]
		public Ingredient[] Ingredients { get; set; }

		[JsonConverter(typeof(IngredientConverter), EnumItemClass.Block, false, new []{ "type", "allowedVariants", "name", "quantity" })]
		public Ingredient Output { get; set; }


		public int ToolDurabilityCost { get; set; } = 1;
		// TODO: public AssetLocation ReplaceBlock { get; } = null;
	}

	public class Ingredient : IEquatable<Ingredient>
	{
		public EnumItemClass Type { get; set; }
		public AssetLocation Code { get; set; }
		public string[] AllowedVariants { get; set; } = null;

		// Name, Quantity and Attributes are not relevant for equality.
		public string Name { get; set; } = null;
		public int Quantity { get; set; } = 1;
		[JsonConverter(typeof(TreeAttributesConverter))]
		public ITreeAttribute Attributes { get; set; } = null;

		internal bool Matches(CollectibleObject collectible)
			=> (Type == collectible?.ItemClass) &&
			   WildcardUtil.Match(Code, collectible.Code, AllowedVariants);

		internal bool Matches(ItemStack stack)
			=> Matches(stack?.Collectible) &&
			   (Attributes?.IsSubSetOf(BuildingOverhaulSystem.API.World, stack.Attributes) ?? true);

		public bool Equals(Ingredient other)
		{
			return (other != null) && (Type == other.Type) && Code.Equals(other.Code) &&
			       ((AllowedVariants == other.AllowedVariants) ||
			        ((AllowedVariants != null) && (other.AllowedVariants != null) &&
			         AllowedVariants.SequenceEqual(other.AllowedVariants)));
		}

		public override bool Equals(object obj)
			=> Equals(obj as Ingredient);

		public override int GetHashCode()
		{
			int hashCode = 1404618148;
			hashCode = hashCode * -1521134295 + Type.GetHashCode();
			hashCode = hashCode * -1521134295 + Code.GetHashCode();
			if (AllowedVariants != null)
			foreach (var variant in AllowedVariants)
				hashCode = hashCode * -1521134295 + variant.GetHashCode();
			return hashCode;
		}

		public static bool operator ==(Ingredient left, Ingredient right)
			=> left?.Equals(right) ?? object.ReferenceEquals(left, right);
		public static bool operator !=(Ingredient left, Ingredient right)
			=> !(left == right);
	}

	internal class IngredientConverter : JsonConverter
	{
		public EnumItemClass DefaultType { get; } = EnumItemClass.Item;
		public bool WildcardSupported { get; }
		public string[] Invalid { get; }

		public IngredientConverter(EnumItemClass type, bool wildcardSupported, string[] invalid)
			{ DefaultType = type; WildcardSupported = wildcardSupported; Invalid = invalid; }

		public override bool CanConvert(Type objectType)
			=> (objectType == typeof(Ingredient));

		public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
		{
			var token = JToken.ReadFrom(reader);
			Ingredient ingredient = null;
			switch (token.Type) {
				case JTokenType.String:
					ingredient = new Ingredient { Type = DefaultType, Code = new AssetLocation((string)token) };
					break;
				case JTokenType.Object:
					var obj = (JObject)token;
					foreach (var invalid in Invalid)
						if (obj.GetValue(invalid, StringComparison.OrdinalIgnoreCase) != null)
							throw new Exception($"'{invalid}' is not applicable");
					ingredient = obj.ToObject<Ingredient>();
					if (obj.GetValue("type", StringComparison.OrdinalIgnoreCase) == null)
						ingredient.Type = DefaultType;
					break;
				default: throw new Exception($"Unexpected JTokenType {token.Type}");
			}
			if (ingredient.Code.IsWildCard && !WildcardSupported)
				throw new Exception("Wildcard not supported");
			return ingredient;
		}

		public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
			=> new NotImplementedException("Writing not supported");
	}

	internal class TreeAttributesConverter : JsonConverter
	{
		public override bool CanConvert(Type objectType)
			=> (objectType == typeof(ITreeAttribute));

		public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
			=> (ITreeAttribute)new JsonObject(JToken.ReadFrom(reader)).ToAttribute();

		public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
			=> new NotImplementedException("Writing not supported");
	}
}
