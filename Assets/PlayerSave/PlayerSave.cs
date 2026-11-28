using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;
using UnityEngine;

// Added by `unity projects create`. One Cloud Save slot for the signed-in
// player, keyed on their player ID by the service. Store the game's save as
// one serializable object, for example a class turned into JSON.
// Use it in Play mode: the sign-in it waits for starts when a scene loads.
public static class PlayerSave
{
    public const string SlotKey = "progress";

    // Saves `data` to the slot. False when the player is not signed in or the
    // save failed.
    public static Task<bool> SaveAsync<T>(T data) => SaveAsync(SlotKey, data);

    // Saves `data` under a custom key (e.g. "social") so independent systems
    // don't overwrite each other's slot.
    public static async Task<bool> SaveAsync<T>(string key, T data)
    {
        try
        {
            if (!await PlayerAuthentication.SignedIn)
            {
                return false;
            }
            await CloudSaveService.Instance.Data.Player.SaveAsync(
                new Dictionary<string, object> { { key, data } });
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Cloud Save failed: {e.Message}");
            return false;
        }
    }

    // Loads the slot, or `fallback` when there is no save yet, the player is
    // not signed in, or the load failed.
    public static Task<T> LoadAsync<T>(T fallback = default) => LoadAsync(SlotKey, fallback);

    // Loads a custom key, or `fallback` (see above).
    public static async Task<T> LoadAsync<T>(string key, T fallback)
    {
        try
        {
            if (!await PlayerAuthentication.SignedIn)
            {
                return fallback;
            }
            var items = await CloudSaveService.Instance.Data.Player.LoadAsync(
                new HashSet<string> { key });
            return items.TryGetValue(key, out Item item) ? item.Value.GetAs<T>() : fallback;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Cloud Save load failed: {e.Message}");
            return fallback;
        }
    }

    // Like LoadAsync, but tells "no save yet" (ok, found = false) apart from
    // "could not ask" (ok = false: not signed in or the load failed), so a
    // caller can avoid overwriting cloud data it never actually read.
    public static async Task<(bool ok, bool found, T value)> TryLoadAsync<T>(string key)
    {
        try
        {
            if (!await PlayerAuthentication.SignedIn)
            {
                return (false, false, default);
            }
            var items = await CloudSaveService.Instance.Data.Player.LoadAsync(
                new HashSet<string> { key });
            return items.TryGetValue(key, out Item item)
                ? (true, true, item.Value.GetAs<T>())
                : (true, false, default);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Cloud Save load failed: {e.Message}");
            return (false, false, default);
        }
    }
}
