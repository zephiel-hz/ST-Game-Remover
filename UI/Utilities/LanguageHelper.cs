using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SteamPluginManager
{
    public static class LanguageHelper
    {
        /// <summary>
        /// Maps TextBlock to resource key for automatic language updates
        /// </summary>
        private static Dictionary<TextBlock, string> _textBlockMappings = new Dictionary<TextBlock, string>();

        /// <summary>
        /// Register a TextBlock to be updated when language changes
        /// </summary>
        public static void RegisterTextBlock(TextBlock textBlock, string resourceKey)
        {
            if (textBlock == null || string.IsNullOrEmpty(resourceKey)) return;
            
            _textBlockMappings[textBlock] = resourceKey;
            
            // Subscribe to unload event to clean up references
            textBlock.Unloaded += (s, e) => UnregisterTextBlock(textBlock);
            
            UpdateTextBlock(textBlock, resourceKey);
        }

        /// <summary>
        /// Unregister a TextBlock to free memory when it's unloaded
        /// </summary>
        public static void UnregisterTextBlock(TextBlock textBlock)
        {
            if (textBlock != null && _textBlockMappings.ContainsKey(textBlock))
            {
                _textBlockMappings.Remove(textBlock);
                Logger.Log($"[LanguageHelper] Unregistered TextBlock");
            }
        }

        /// <summary>
        /// Update a single TextBlock with value from resources
        /// </summary>
        public static void UpdateTextBlock(TextBlock textBlock, string resourceKey)
        {
            try
            {
                if (textBlock == null || string.IsNullOrEmpty(resourceKey)) return;
                
                var resources = Application.Current.Resources;
                
                // First try direct resource lookup
                if (resources.Contains(resourceKey))
                {
                    var value = resources[resourceKey]?.ToString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        textBlock.Text = value;
                        return;
                    }
                }
                
                // If not found, search in merged dictionaries (for string resources)
                foreach (var dict in resources.MergedDictionaries)
                {
                    if (dict.Contains(resourceKey))
                    {
                        var value = dict[resourceKey]?.ToString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            textBlock.Text = value;
                            return;
                        }
                    }
                }
                
                Logger.Log($"[LanguageHelper] Resource key '{resourceKey}' not found in any dictionary");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to update TextBlock for key {resourceKey}: {ex.Message}");
            }
        }

        /// <summary>
        /// Update all registered TextBlocks with new language
        /// </summary>
        public static void UpdateAllRegisteredTextBlocks()
        {
            try
            {
                Logger.Log($"[LanguageHelper] Updating {_textBlockMappings.Count} registered TextBlocks");
                
                foreach (var kvp in _textBlockMappings)
                {
                    UpdateTextBlock(kvp.Key, kvp.Value);
                }
                
                Logger.Log("[LanguageHelper] All TextBlocks updated");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to update TextBlocks: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear all registrations
        /// </summary>
        public static void ClearRegistrations()
        {
            _textBlockMappings.Clear();
        }
    }
}
