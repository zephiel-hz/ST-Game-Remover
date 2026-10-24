using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SteamPluginManager.Services.Security
{
    /// <summary>
    /// Layanan terpusat untuk menghasilkan dan menyimpan Device ID yang persisten,
    /// deterministik, dan anti-ubah. Menghindari nilai default OEM seperti 'Not applicable'
    /// atau 'Unknown' dan perubahan adapter jaringan (Wi-Fi/LAN/VPN).
    /// </summary>
    public static class DeviceIdentifier
    {
        private static string? _cachedDeviceId;
        private static readonly object _lock = new();

        private const string RegistrySubKey = @"Software\HZLuaManager";
        private const string LegacyRegistrySubKey = @"Software\SteamPluginManager";
        private const string DeviceIdValueName = "DeviceId";

        private static readonly HashSet<string> InvalidValues = new(StringComparer.OrdinalIgnoreCase)
        {
            "0",
            "0000000",
            "00000000",
            "000000000000",
            "00000000-0000-0000-0000-000000000000",
            "ffffffff-ffff-ffff-ffff-ffffffffffff",
            "123456789",
            "default string",
            "default",
            "system default",
            "not applicable",
            "not available",
            "not specified",
            "unknown",
            "system reserved",
            "pending",
            "to be filled by o.e.m.",
            "to be filled by oem",
            "o.e.m.",
            "oem",
            "empty",
            "none",
            "null",
            "n/a",
            "na",
            "chassis serial number",
            "system serial number",
            "base board serial number",
            "serial number"
        };

        /// <summary>
        /// Mengambil Device ID yang stabil dan dijamin valid.
        /// Menggunakan in-memory cache, Registry HKCU, dan backup file AppData.
        /// </summary>
        public static string GetDeviceId()
        {
            if (!string.IsNullOrEmpty(_cachedDeviceId))
                return _cachedDeviceId;

            lock (_lock)
            {
                if (!string.IsNullOrEmpty(_cachedDeviceId))
                    return _cachedDeviceId;

                string id = ResolveAndPersistDeviceId();
                _cachedDeviceId = id;
                return id;
            }
        }

        /// <summary>
        /// Memvalidasi apakah string Device ID atau Serial Number valid dan bukan nilai placeholder/junk.
        /// </summary>
        public static bool IsValidDeviceId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            string clean = id.Trim();
            if (clean.Length < 4)
                return false;

            if (InvalidValues.Contains(clean))
                return false;

            // Tolak jika hanya terdiri dari angka nol, tanda hubung, titik dua, atau spasi
            string stripped = clean.Replace("0", "").Replace("-", "").Replace(" ", "").Replace(":", "");
            if (string.IsNullOrEmpty(stripped))
                return false;

            return true;
        }

        private static string ResolveAndPersistDeviceId()
        {
            // 1. Coba baca dari penyimpanan lokal yang sudah ada (Registry & AppData)
            string? storedId = ReadPersistedDeviceId();
            if (IsValidDeviceId(storedId))
            {
                // Pastikan tersinkronisasi ke Registry dan AppData jika salah satu hilang
                PersistDeviceId(storedId!);
                return storedId!;
            }

            // 2. Jika belum tersimpan atau nilai lama invalid (seperti 'Not applicable' / 'UNKNOWN'),
            // kita bangun ID deterministik berdasarkan Hardware & OS
            string resolvedId = GenerateHardwareOsFingerprint();

            // 3. Simpan permanen ke Registry & AppData
            PersistDeviceId(resolvedId);

            return resolvedId;
        }

        private static string? ReadPersistedDeviceId()
        {
            try
            {
                // Coba Registry HKCU\Software\HZLuaManager
                using (var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey))
                {
                    var val = key?.GetValue(DeviceIdValueName)?.ToString();
                    if (IsValidDeviceId(val))
                        return val!.Trim();
                }

                // Coba Registry Legacy HKCU\Software\SteamPluginManager
                using (var key = Registry.CurrentUser.OpenSubKey(LegacyRegistrySubKey))
                {
                    var val = key?.GetValue(DeviceIdValueName)?.ToString();
                    if (IsValidDeviceId(val))
                        return val!.Trim();
                }

                // Coba File AppData HZLuaManager
                string localPath = GetAppDataFilePath("HZLuaManager");
                if (File.Exists(localPath))
                {
                    string content = File.ReadAllText(localPath).Trim();
                    if (IsValidDeviceId(content))
                        return content;
                }

                // Coba File AppData SteamPluginManager
                string legacyPath = GetAppDataFilePath("SteamPluginManager");
                if (File.Exists(legacyPath))
                {
                    string content = File.ReadAllText(legacyPath).Trim();
                    if (IsValidDeviceId(content))
                        return content;
                }
            }
            catch
            {
                // Abaikan error pembacaan storage
            }

            return null;
        }

        private static void PersistDeviceId(string deviceId)
        {
            try
            {
                // Simpan ke Registry HKCU\Software\HZLuaManager
                using (var key = Registry.CurrentUser.CreateSubKey(RegistrySubKey))
                {
                    key?.SetValue(DeviceIdValueName, deviceId, RegistryValueKind.String);
                }

                // Simpan juga ke Registry Legacy untuk kompatibilitas
                using (var key = Registry.CurrentUser.CreateSubKey(LegacyRegistrySubKey))
                {
                    key?.SetValue(DeviceIdValueName, deviceId, RegistryValueKind.String);
                }
            }
            catch { }

            try
            {
                // Simpan ke AppData\Local\HZLuaManager\device.id
                string localPath = GetAppDataFilePath("HZLuaManager");
                string? dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(localPath, deviceId);

                // Simpan juga ke AppData Legacy
                string legacyPath = GetAppDataFilePath("SteamPluginManager");
                string? legacyDir = Path.GetDirectoryName(legacyPath);
                if (!string.IsNullOrEmpty(legacyDir) && !Directory.Exists(legacyDir))
                {
                    Directory.CreateDirectory(legacyDir);
                }
                File.WriteAllText(legacyPath, deviceId);
            }
            catch { }
        }

        private static string GetAppDataFilePath(string appFolderName)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, appFolderName, "device.id");
        }

        private static string GenerateHardwareOsFingerprint()
        {
            // A. Cek apakah motherboard memiliki Serial Number ASLI yang valid
            // Jika ada serial motherboard asli (bukan 'Not applicable', bukan OEM default),
            // kita prioritaskan agar user yang sudah terikat token lama tidak perlu verifikasi ulang.
            string? mbSerial = GetRawMotherboardSerial();
            if (IsValidDeviceId(mbSerial))
            {
                return mbSerial!.Trim();
            }

            // B. Jika Motherboard Serial tidak valid (misal 'Not applicable' / 'To be filled by O.E.M.'),
            // gunakan Windows Cryptography MachineGuid + BIOS UUID + CPU ProcessorId.
            // MachineGuid dibuat otomatis oleh Windows saat instalasi OS dan selalu ada.
            string? machineGuid = GetWindowsMachineGuid();
            string? biosUuid = GetBiosUuid();
            string? cpuId = GetCpuProcessorId();

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(machineGuid))
                sb.Append("MG:").Append(machineGuid.Trim());
            if (!string.IsNullOrWhiteSpace(biosUuid))
                sb.Append("|BIOS:").Append(biosUuid.Trim());
            if (!string.IsNullOrWhiteSpace(cpuId))
                sb.Append("|CPU:").Append(cpuId.Trim());

            string combined = sb.ToString();

            // Jika karena suatu hal tidak ada hardware info sama sekali (ekstrem), buat GUID acak baru
            if (string.IsNullOrWhiteSpace(combined))
            {
                return Guid.NewGuid().ToString("D").ToUpperInvariant();
            }

            // Hash kombinasi menjadi GUID deterministik yang selalu sama seumur OS/PC ini
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
            byte[] guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, 16);

            return new Guid(guidBytes).ToString("D").ToUpperInvariant();
        }

        private static string? GetWindowsMachineGuid()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var val = key?.GetValue("MachineGuid")?.ToString();
                if (IsValidDeviceId(val))
                    return val;
            }
            catch { }

            return null;
        }

        private static string? GetRawMotherboardSerial()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("Select SerialNumber from Win32_BaseBoard");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string? serial = obj["SerialNumber"]?.ToString();
                    if (IsValidDeviceId(serial))
                    {
                        return serial;
                    }
                }
            }
            catch { }

            return null;
        }

        private static string? GetBiosUuid()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("Select UUID from Win32_ComputerSystemProduct");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string? uuid = obj["UUID"]?.ToString();
                    if (IsValidDeviceId(uuid))
                    {
                        return uuid;
                    }
                }
            }
            catch { }

            return null;
        }

        private static string? GetCpuProcessorId()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("Select ProcessorId from Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string? cpuId = obj["ProcessorId"]?.ToString();
                    if (IsValidDeviceId(cpuId))
                    {
                        return cpuId;
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
