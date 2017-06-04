﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SubjectNerd.PsdImporter.PsdParser;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SubjectNerd.PsdImporter
{
	public class PsdImporter
	{
		private const string DOC_ROOT = "DOCUMENT_ROOT";

		private static string GetPsdFilepath(Object psdFile)
		{
			string filepath = AssetDatabase.GetAssetPath(psdFile);
			if (string.IsNullOrEmpty(filepath))
				return string.Empty;
			if (filepath.ToLower().EndsWith(".psd") == false)
				return string.Empty;
			return filepath;
		}

		private static IEnumerator ParseLayers(IPsdLayer[] layers, bool doYield,
			Action<PsdLayer, int[]> onLayer, Action onComplete, int[] parentIndex = null)
		{
			// Loop through layers in reverse so they are encountered in same order as Photoshop
			for (int i = layers.Length - 1; i >= 0; i--)
			{
				int[] layerIndex = parentIndex;
				if (layerIndex == null)
				{
					layerIndex = new int[] {i};
				}
				else
				{
					int lastIndex = layerIndex.Length;
					Array.Resize(ref layerIndex, lastIndex + 1);
					layerIndex[lastIndex] = i;
				}
				
				PsdLayer layer = layers[i] as PsdLayer;
				if (layer == null)
					continue;

				if (onLayer != null)
					onLayer(layer, layerIndex);

				if (doYield)
					yield return null;

				if (layer.Childs.Length > 0)
				{
					yield return EditorCoroutineRunner.StartCoroutine(
						ParseLayers(layer.Childs, doYield, onLayer, null, layerIndex)
					);
				}
			}

			if (onComplete != null)
				onComplete();
		}

		public static void BuildImportLayerData(Object file, ImportUserData importSettings,
												Action<ImportLayerData, DisplayLayerData> callback)
		{
			string filepath = GetPsdFilepath(file);
			if (string.IsNullOrEmpty(filepath))
			{
				if (callback != null)
					callback(null, null);
				return;
			}
			
			using (PsdDocument psd = PsdDocument.Create(filepath))
			{
				ImportLayerData docImportData = new ImportLayerData()
				{
					name = DOC_ROOT,
					indexId = new int[] {-1},
					Childs = new List<ImportLayerData>()
				};
				DisplayLayerData docDisplayData = new DisplayLayerData()
				{
					indexId = new int[] {-1},
					Childs = new List<DisplayLayerData>()
				};

				EditorCoroutineRunner.StartCoroutine(
					ParseLayers(psd.Childs, false,
					onLayer: (layer, indexId) =>
					{
						// Walk down the index id to get the parent layers
						// and build the full path
						string fullPath = "";
						ImportLayerData parentLayer = docImportData;
						DisplayLayerData parentDisplay = docDisplayData;
						if (indexId.Length > 1)
						{
							for (int idIdx = 0; idIdx < indexId.Length - 1; idIdx++)
							{
								int idx = indexId[idIdx];
								parentLayer = parentLayer.Childs[idx];
								parentDisplay = parentDisplay.Childs[idx];

								if (string.IsNullOrEmpty(fullPath) == false)
									fullPath += "/";
								fullPath += parentLayer.name;
							}
						}
						
						if (string.IsNullOrEmpty(fullPath) == false)
							fullPath += "/";
						fullPath += layer.Name;

						ImportLayerData layerImportData = new ImportLayerData()
						{
							name = layer.Name,
							path = fullPath,
							indexId = indexId,
							import = layer.IsVisible,
							useDefaults = true,
							Alignment = importSettings.DefaultAlignment,
							Pivot = importSettings.DefaultPivot,
							ScaleFactor = importSettings.ScaleFactor,
							Childs = new List<ImportLayerData>()
						};
						
						DisplayLayerData layerDisplayData = new DisplayLayerData()
						{
							indexId = indexId,
							isVisible = layer.IsVisible,
							isGroup = layer.Childs.Length > 0,
							isOpen = layer.IsFolderOpen
						};

						int layerIdx = indexId[indexId.Length - 1];
						
						int maxLayers = layerIdx + 1;
						while (parentLayer.Childs.Count < maxLayers)
							parentLayer.Childs.Add(null);

						parentLayer.Childs[layerIdx] = layerImportData;

						while (parentDisplay.Childs.Count < maxLayers)
							parentDisplay.Childs.Add(null);

						parentDisplay.Childs[layerIdx] = layerDisplayData;
					},
					onComplete: () =>
					{
						if (callback != null)
							callback(docImportData, docDisplayData);
					})
				);
			}
		}

		private static PsdLayer GetPsdLayerByIndex(PsdDocument psdDoc, int[] layerIdx)
		{
			IPsdLayer target = psdDoc;
			foreach (int idx in layerIdx)
			{
				if (idx < 0 || idx >= target.Childs.Length)
					return null;
				target = target.Childs[idx];
			}

			PsdLayer layer = target as PsdLayer;
			return layer;
		}

		#region Layer Texture Generation
		public static Texture2D GetLayerTexture(Object psdFile, int[] layerIdx)
		{
			ImportLayerData setting = new ImportLayerData()
			{
				Alignment = SpriteAlignment.Center,
				Pivot = new Vector2(0.5f, 0.5f),
				ScaleFactor = ScaleFactor.Full,
                Childs = new List<ImportLayerData>(),
				import = true,
				indexId = layerIdx
			};
			return GetLayerTexture(psdFile, setting);
		}

		public static Texture2D GetLayerTexture(Object psdFile, ImportLayerData setting)
		{
			string filepath = GetPsdFilepath(psdFile);
			if (string.IsNullOrEmpty(filepath))
				return null;

			Texture2D texture = null;
			using (PsdDocument psd = PsdDocument.Create(filepath))
			{
				var layer = GetPsdLayerByIndex(psd, setting.indexId);
				texture = GetLayerTexture(psd, layer, setting);
			}
			return texture;
		}

		private static Texture2D GetLayerTexture(PsdDocument psdDoc, PsdLayer psdLayer, ImportLayerData setting)
		{
			if (psdLayer == null || psdLayer.IsGroup)
				return null;

			Texture2D layerTexture = GetTexture(psdLayer);
			if (setting.ScaleFactor != ScaleFactor.Full)
			{
				int mipMapLevel = setting.ScaleFactor == ScaleFactor.Half ? 1 : 2;
				layerTexture = ScaleTextureByMipmap(layerTexture, mipMapLevel);
			}
			return layerTexture;
		}

		private static Texture2D GetTexture(PsdLayer layer)
		{
			Texture2D texture = new Texture2D(layer.Width, layer.Height);
			Color32[] pixels = new Color32[layer.Width * layer.Height];

			Channel red = (from l in layer.Channels where l.Type == ChannelType.Red select l).First();
			Channel green = (from l in layer.Channels where l.Type == ChannelType.Green select l).First();
			Channel blue = (from l in layer.Channels where l.Type == ChannelType.Blue select l).First();
			Channel alpha = (from l in layer.Channels where l.Type == ChannelType.Alpha select l).FirstOrDefault();
			Channel mask = (from l in layer.Channels where l.Type == ChannelType.Mask select l).FirstOrDefault();

			for (int i = 0; i < pixels.Length; i++)
			{
				byte r = red.Data[i];
				byte g = green.Data[i];
				byte b = blue.Data[i];
				byte a = 255;

				if (alpha != null)
					a = alpha.Data[i];
				if (mask != null)
					a *= mask.Data[i];

				int mod = i % texture.width;
				int n = ((texture.width - mod - 1) + i) - mod;
				pixels[pixels.Length - n - 1] = new Color32(r, g, b, a);
			}

			texture.SetPixels32(pixels);
			texture.Apply();
			return texture;
		}

		private static Texture2D ScaleTextureByMipmap(Texture2D tex, int mipLevel)
		{
			if (mipLevel < 0 || mipLevel > 2)
				return null;
			int width = Mathf.RoundToInt(tex.width / (mipLevel * 2));
			int height = Mathf.RoundToInt(tex.height / (mipLevel * 2));

			// Scaling down by abusing mip maps
			Texture2D resized = new Texture2D(width, height);
			resized.SetPixels32(tex.GetPixels32(mipLevel));
			resized.Apply();
			return resized;
		}
		#endregion

		#region Layer Asset Import
		public static void ImportLayersUI(Object psdFile, ImportUserData importSettings, List<int[]> layerIndices)
		{
			int total = layerIndices.Count;
			EditorCoroutineRunner.StartCoroutineWithUI(
				ImportCoroutine(psdFile, importSettings, layerIndices, true,
					layerCallback: (current, layer) =>
					{
						string text = string.Format("[{0}/{1}] Layer: {2}", current, total, layer.name);
						float percent = (float)current/total;
						EditorCoroutineRunner.UpdateUI(text, percent);
					}
				), "Importing PSD Layers", true
			);
		}

		public static void ImportLayers(Object psdFile, ImportUserData importSettings, List<int[]> layerIndices, Action<List<Sprite>> callback = null)
		{
			EditorCoroutineRunner.StartCoroutine(
				ImportCoroutine(psdFile, importSettings, layerIndices, false, completeCallback: callback)
			);
		}

		private static IEnumerator ImportCoroutine(Object psdFile, ImportUserData importSettings,
													List<int[]> layerIndices, bool doYield = false,
													Action<int, ImportLayerData> layerCallback = null,
													Action<List<Sprite>> completeCallback = null)
		{
			string filepath = GetPsdFilepath(psdFile);
			if (string.IsNullOrEmpty(filepath))
			{
				if (completeCallback != null)
					completeCallback(null);
				yield break;
			}

			// No target directory set, use PSD file directory
			if (string.IsNullOrEmpty(importSettings.TargetDirectory))
				importSettings.TargetDirectory = filepath.Substring(0, filepath.LastIndexOf("/"));

			// Get the texture importer for the PSD
			TextureImporter psdUnitySettings = (TextureImporter)AssetImporter.GetAtPath(filepath);
			TextureImporterSettings psdUnityImport = new TextureImporterSettings();
			psdUnitySettings.ReadTextureSettings(psdUnityImport);

			int importCurrent = 0;

			List<Sprite> sprites = new List<Sprite>();
			using (PsdDocument psd = PsdDocument.Create(filepath))
			{
				foreach (int[] layerIdx in layerIndices)
				{
					ImportLayerData layerSettings = importSettings.GetLayerData(layerIdx);
					if (layerSettings == null)
						continue;

					if (layerCallback != null)
						layerCallback(importCurrent, layerSettings);

					var sprite = ImportLayer(psd, importSettings, layerSettings, psdUnityImport);
					sprites.Add(sprite);
					importCurrent++;

					if (doYield)
						yield return null;
				}
			}
			if (completeCallback != null)
				completeCallback(sprites);
		}

		private static Sprite ImportLayer(PsdDocument psdDoc, ImportUserData importSettings, ImportLayerData layerSettings, TextureImporterSettings psdUnityImport)
		{
			if (layerSettings == null)
				return null;

			PsdLayer psdLayer = GetPsdLayerByIndex(psdDoc, layerSettings.indexId);
			if (psdLayer.IsGroup)
				return null;
			
			// Generate the texture
			Texture2D layerTexture = GetLayerTexture(psdDoc, psdLayer, layerSettings);
			if (layerTexture == null)
				return null;

			// Save the texture as an asset
			Sprite layerSprite = SaveAsset(psdLayer, psdUnityImport, layerTexture, importSettings, layerSettings);
			return layerSprite;
		}

		private static Sprite SaveAsset(PsdLayer psdLayer, TextureImporterSettings psdUnityImport,
									Texture2D texture, ImportUserData importSettings, ImportLayerData layerSettings)
		{
			// Generate the file path for this layer
			string fileDir;
			string filepath = GetFilePath(psdLayer, importSettings, out fileDir);

			// Create the folder if non existent
			if (AssetDatabase.IsValidFolder(fileDir) == false)
			{
				int lastSeparator = fileDir.LastIndexOf("/");
				string parentFolder = fileDir.Substring(0, lastSeparator);
				string createDir = fileDir.Substring(lastSeparator + 1);
				AssetDatabase.CreateFolder(parentFolder, createDir);
			}

			// Write out the texture contents into the file
			AssetDatabase.CreateAsset(texture, filepath);
			byte[] buf = texture.EncodeToPNG();
			File.WriteAllBytes(filepath, buf);

			AssetDatabase.ImportAsset(filepath, ImportAssetOptions.ForceUpdate);
			Texture2D textureObj = AssetDatabase.LoadAssetAtPath<Texture2D>(filepath);

			// Get the texture importer for the asset
			TextureImporter textureImporter = (TextureImporter)AssetImporter.GetAtPath(filepath);
			// Read out the texture import settings so settings can be changed
			TextureImporterSettings texSetting = new TextureImporterSettings();
			textureImporter.ReadTextureSettings(texSetting);

			// Change settings
			texSetting.spriteAlignment = (int)layerSettings.Alignment;
			texSetting.spritePivot = layerSettings.Pivot;
			texSetting.spritePixelsPerUnit = psdUnityImport.spritePixelsPerUnit;
			texSetting.filterMode = psdUnityImport.filterMode;
			texSetting.wrapMode = psdUnityImport.wrapMode;
			texSetting.textureType = TextureImporterType.Sprite;
			texSetting.spriteMode = (int)SpriteImportMode.Single;
			texSetting.mipmapEnabled = false;
			texSetting.alphaIsTransparency = true;
			texSetting.npotScale = TextureImporterNPOTScale.None;
			// Set the rest of the texture settings
			textureImporter.spritePackingTag = importSettings.PackingTag;
			// Write in the texture import settings
			textureImporter.SetTextureSettings(texSetting);

			EditorUtility.SetDirty(textureObj);
			AssetDatabase.WriteImportSettingsIfDirty(filepath);
			AssetDatabase.ImportAsset(filepath, ImportAssetOptions.ForceUpdate);
			return (Sprite)AssetDatabase.LoadAssetAtPath(filepath, typeof(Sprite));
		}

		public static string GetFilePath(PsdLayer layer, ImportUserData importSettings, out string dir)
		{
			string filename = string.Format("{0}.png", layer.Name);

			string folder = "";

			if (importSettings.fileNaming != NamingConvention.LayerNameOnly)
			{
				bool isDir = importSettings.fileNaming == NamingConvention.CreateGroupFolders;
				var docLayer = layer.Document as IPsdLayer;
				var parent = layer.Parent;
				while (parent != null && parent.Equals(docLayer) == false)
				{
					if (isDir)
					{
						if (string.IsNullOrEmpty(folder))
							folder = parent.Name;
						else
							folder = string.Format("{0}/{1}", parent.Name, folder);
					}
					else
					{
						filename = string.Format("{0}_{1}", parent.Name, filename);
					}
                    parent = parent.Parent;
					if (importSettings.groupMode == GroupMode.ParentOnly)
						break;
				}
			}

			string finalDir = importSettings.TargetDirectory;
			if (string.IsNullOrEmpty(folder) == false)
				finalDir = string.Format("{0}/{1}", finalDir, folder);
			// Sanitize directory
			finalDir = CleanText(finalDir, Path.GetInvalidPathChars());

			// Sanitize filename
			filename = CleanText(filename, Path.GetInvalidFileNameChars());

			string filepath = string.Format("{0}/{1}", finalDir, filename);
			dir = finalDir;
			return filepath;
		}

		private static string CleanText(string text, char[] cleanChars)
		{
			text = string.Join("_", text.Split(cleanChars));
			text = new string(text.Select(c =>
			{
				if (char.IsWhiteSpace(c))
					return '_';
				return c;
			}).ToArray());
			return text;
		}
		#endregion
	}
}