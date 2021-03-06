using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace BuildingOverhaul
{
	[HarmonyPatch(typeof(SystemMouseInWorldInteractions), "HandleMouseInteractionsBlockSelected")]
	static class SystemMouseInWorldInteractions_HandleMouseInteractionsBlockSelected_Patch
	{
		public static System.Func<bool> InWorldInteract = null!;
		public static bool OnInWorldInteract() => InWorldInteract();

		static IEnumerable<CodeInstruction> Transpiler(
			IEnumerable<CodeInstruction> instructions,
			ILGenerator generator)
		{
			var enumerator = instructions.GetEnumerator();
			var beginOnInWorldInteraction = generator.DefineLabel();

			// Yield instructions until the first call to the EntityControls.Sneak getter.
			var GetSneak = typeof(EntityControls).GetProperty(nameof(EntityControls.Sneak)).GetMethod;
			while (enumerator.MoveNext()) {
				yield return enumerator.Current;
				if (enumerator.Current.Is(OpCodes.Callvirt, GetSneak)) break;
			}

			// Next instruction is "brtrue.s".
			enumerator.MoveNext();
			// Extract the instruction's label.
			var afterOnInWorldInteraction = (Label)enumerator.Current.operand;
			// Replace this instruction with one that instead jumps
			// to the beginning of the InWorldInteraction call.
			yield return new(OpCodes.Brtrue_S, beginOnInWorldInteraction);

			// Yield further instructions until the call to TryBeginUseBlock.
			var TryBeginUseBlock = typeof(SystemMouseInWorldInteractions).GetMethod(
				"TryBeginUseBlock", BindingFlags.Instance | BindingFlags.NonPublic);
			while (enumerator.MoveNext()) {
				yield return enumerator.Current;
				if (enumerator.Current.Is(OpCodes.Call, TryBeginUseBlock)) break;
			}

			// Next instruction is "brfalse.s"
			enumerator.MoveNext();
			// Also replace this instruction with one that jumps
			// to the beginning of the InWorldInteraction call.
			yield return new(OpCodes.Brfalse_S, beginOnInWorldInteraction);
			// Next instruction is "ret", just yield it.
			enumerator.MoveNext();
			yield return enumerator.Current;

			// Insert call to OnInWorldInteract. If it returns true, return from the method immediately.
			var _OnInWorldInteract = typeof(SystemMouseInWorldInteractions_HandleMouseInteractionsBlockSelected_Patch).GetMethod(nameof(OnInWorldInteract));
			yield return new(OpCodes.Call, _OnInWorldInteract){ labels = new(){ beginOnInWorldInteraction } };
			yield return new(OpCodes.Brfalse_S, afterOnInWorldInteraction);
			yield return new(OpCodes.Ret);

			// Yield the rest of the instructions.
			while (enumerator.MoveNext())
				yield return enumerator.Current;
		}
	}
}
