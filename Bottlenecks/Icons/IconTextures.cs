using System.IO;
using System.Reflection;
using UnityEngine;

namespace Bottlenecks.Icons
{
	public static class IconTextures
	{
		private static bool s_LoggedReferenceSpec;

		public static Texture2D CreateIconTexture(string resourceName, Texture2D reference)
		{
			if (reference == null)
			{
				Mod.log.Warn($"IconTextures: cannot create '{resourceName}', reference texture is null.");
				return null;
			}

			LogReferenceSpecOnce(reference);

			byte[] bytes = ReadResourceBytes(resourceName);
			if (bytes == null)
			{
				Mod.log.Warn($"IconTextures: embedded resource '{resourceName}' not found.");
				return null;
			}

			Texture2D source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
			if (!ImageConversion.LoadImage(source, bytes))
			{
				UnityEngine.Object.Destroy(source);
				Mod.log.Warn($"IconTextures: failed to decode '{resourceName}'.");
				return null;
			}

			Texture2D resized = Resize(source, reference.width, reference.height, reference.mipmapCount > 1);
			UnityEngine.Object.Destroy(source);

			if (resized == null)
			{
				Mod.log.Warn($"IconTextures: failed to resize '{resourceName}'.");
				return null;
			}

			Texture2D converted = ConvertFormat(resized, reference.format, reference.mipmapCount > 1);
			if (!ReferenceEquals(converted, resized))
			{
				UnityEngine.Object.Destroy(resized);
			}

			if (converted == null)
			{
				Mod.log.Warn($"IconTextures: cannot produce format {reference.format} for '{resourceName}'.");
				return null;
			}

			if (converted.width != reference.width
				|| converted.height != reference.height
				|| converted.format != reference.format
				|| converted.mipmapCount != reference.mipmapCount)
			{
				Mod.log.Warn($"IconTextures: '{resourceName}' produced {converted.width}x{converted.height} {converted.format} mips={converted.mipmapCount}, but reference is {reference.width}x{reference.height} {reference.format} mips={reference.mipmapCount}.");
				UnityEngine.Object.Destroy(converted);
				return null;
			}

			converted.name = resourceName;
			return converted;
		}

		private static void LogReferenceSpecOnce(Texture2D reference)
		{
			if (s_LoggedReferenceSpec)
			{
				return;
			}

			s_LoggedReferenceSpec = true;
			Mod.log.Info($"IconTextures: reference icon spec {reference.width}x{reference.height} {reference.format} mipmapCount={reference.mipmapCount}");
		}

		private static byte[] ReadResourceBytes(string resourceName)
		{
			Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
			if (stream == null)
			{
				return null;
			}

			using (stream)
			using (MemoryStream memory = new MemoryStream())
			{
				stream.CopyTo(memory);
				return memory.ToArray();
			}
		}

		private static Texture2D Resize(Texture2D source, int width, int height, bool mipChain)
		{
			RenderTexture temp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			RenderTexture previous = RenderTexture.active;
			RenderTexture.active = temp;
			Graphics.Blit(source, temp);

			Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, mipChain);
			result.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
			result.Apply(true);

			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(temp);
			return result;
		}

		private static Texture2D ConvertFormat(Texture2D texture, TextureFormat target, bool mipChain)
		{
			if (texture.format == target)
			{
				return texture;
			}

			if (target == TextureFormat.ARGB32 || target == TextureFormat.BGRA32)
			{
				Texture2D converted = new Texture2D(texture.width, texture.height, target, mipChain);
				converted.SetPixels32(texture.GetPixels32());
				converted.Apply(true);
				return converted;
			}

			if (target == TextureFormat.DXT5 || target == TextureFormat.DXT1)
			{
				texture.Compress(true);
				return texture;
			}

			return null;
		}
	}
}
