using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using NAudio.Wave;

namespace FifFloat
{
    public sealed class RecordingClip
    {
        public string FilePath;
        public string Name;
        public double DurationSeconds;
        public bool AutoNamed;
    }

    public sealed class RecordingLibrary
    {
        public sealed class StoredClip { public string FileName; public string Name; public bool? AutoNamed; }
        public sealed class Document
        {
            public int Version = 2;
            public int NextNumber = 1;
            public List<StoredClip> Clips = new List<StoredClip>();
            public List<string> HiddenFiles = new List<string>();
        }

        private readonly object sync = new object();
        private readonly string directory, indexFile;
        private readonly List<RecordingClip> clips = new List<RecordingClip>();
        private readonly HashSet<string> hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int nextNumber = 1;

        public RecordingLibrary(string dataDirectory)
        {
            directory = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(directory);
            indexFile = Path.Combine(directory, "录音列表.json");
            Document saved = ReadDocument(indexFile);
            bool recoveredFromBackup = false;
            if (saved == null)
            {
                saved = ReadDocument(indexFile + ".bak");
                recoveredFromBackup = saved != null;
            }
            if (saved != null)
            {
                nextNumber = Math.Max(1, saved.NextNumber);
                foreach (string name in saved.HiddenFiles ?? new List<string>())
                    if (IsSafeFileName(name)) hidden.Add(name);
                foreach (StoredClip item in saved.Clips ?? new List<StoredClip>())
                {
                    if (item == null || !IsSafeFileName(item.FileName)) continue;
                    string full = Path.Combine(directory, item.FileName);
                    if (clips.Exists(delegate(RecordingClip clip) { return SamePath(clip.FilePath, full); })) continue;
                    try { clips.Add(ReadClip(full, CleanName(item.Name), item.AutoNamed ?? IsLegacyDefaultName(item.Name, saved.NextNumber))); }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (InvalidDataException) { }
                    catch (InvalidOperationException) { }
                    catch (ArgumentException) { }
                    catch (FormatException) { }
                }
            }
            string[] files = Directory.GetFiles(directory, "*.wav");
            Array.Sort(files, delegate(string left, string right)
            { return File.GetLastWriteTimeUtc(left).CompareTo(File.GetLastWriteTimeUtc(right)); });
            foreach (string file in files)
            {
                if (hidden.Contains(Path.GetFileName(file)) || clips.Exists(delegate(RecordingClip clip) { return SamePath(clip.FilePath, file); })) continue;
                try { clips.Add(ReadClip(file, "录音 " + nextNumber)); nextNumber++; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (InvalidDataException) { }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
                catch (FormatException) { }
            }
            RenumberAutomatic();
            SaveCore(recoveredFromBackup);
        }

        private static bool SamePath(string left, string right)
        { return String.Equals(left, right, StringComparison.OrdinalIgnoreCase); }

        private static bool IsSafeFileName(string name)
        {
            return !String.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                name == Path.GetFileName(name) &&
                String.Equals(Path.GetExtension(name), ".wav", StringComparison.OrdinalIgnoreCase);
        }

        private Document ReadDocument(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
                Document doc = new JavaScriptSerializer().Deserialize<Document>(File.ReadAllText(path, Encoding.UTF8));
                if (doc != null && doc.Version != 1 && doc.Version != 2)
                    throw new NotSupportedException("录音列表由更新版本创建，请使用相应版本打开；原列表未作修改。");
                return doc;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static string CleanName(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return "未命名录音";
            string clean = name.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return clean.Length > 80 ? clean.Substring(0, 80) : clean;
        }

        private string OwnPath(string filePath)
        {
            string full = Path.GetFullPath(filePath);
            if (!full.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetDirectoryName(full), directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请先通过导入功能把录音加入本机录音目录。");
            return full;
        }

        private static bool IsLegacyDefaultName(string name, int savedNextNumber)
        {
            int number;
            return name != null && name.StartsWith("录音 ", StringComparison.Ordinal) &&
                Int32.TryParse(name.Substring(3), out number) && number > 0 && number < savedNextNumber;
        }

        private void RenumberAutomatic()
        {
            int number = 1;
            foreach (RecordingClip clip in clips)
                if (clip.AutoNamed) clip.Name = "录音 " + number++;
            nextNumber = number;
        }

        private void RestoreClips(List<RecordingClip> saved, int previousNumber)
        {
            clips.Clear();
            clips.AddRange(saved);
            nextNumber = previousNumber;
        }

        private static RecordingClip ReadClip(string full, string name, bool autoNamed = true)
        {
            AudioEngine.ValidateWav(full);
            using (WaveFileReader wav = new WaveFileReader(full))
                return new RecordingClip { FilePath = full, Name = name, DurationSeconds = wav.TotalTime.TotalSeconds, AutoNamed = autoNamed };
        }

        private static RecordingClip Copy(RecordingClip clip)
        { return new RecordingClip { FilePath = clip.FilePath, Name = clip.Name, DurationSeconds = clip.DurationSeconds, AutoNamed = clip.AutoNamed }; }

        public List<RecordingClip> Items()
        {
            lock (sync) { return clips.ConvertAll(delegate(RecordingClip clip) { return Copy(clip); }); }
        }

        public RecordingClip Add(string filePath)
        {
            lock (sync)
            {
                string full = OwnPath(filePath);
                RecordingClip previous = clips.Find(delegate(RecordingClip clip) { return SamePath(clip.FilePath, full); });
                if (previous != null) return Copy(previous);
                RecordingClip fresh = ReadClip(full, "录音 " + nextNumber);
                string fileName = Path.GetFileName(full);
                bool wasHidden = hidden.Remove(fileName);
                clips.Add(fresh);
                nextNumber++;
                try { SaveCore(); }
                catch
                {
                    clips.Remove(fresh);
                    nextNumber--;
                    if (wasHidden) hidden.Add(fileName);
                    throw;
                }
                return Copy(fresh);
            }
        }

        public void Rename(string filePath, string name)
        {
            lock (sync)
            {
                if (String.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("录音名称不能为空。");
                string full = OwnPath(filePath);
                RecordingClip clip = clips.Find(delegate(RecordingClip item) { return SamePath(item.FilePath, full); });
                if (clip == null) throw new InvalidOperationException("该录音已不在列表中，请刷新列表。");
                List<RecordingClip> previous = Items();
                int previousNumber = nextNumber;
                clip.Name = CleanName(name);
                clip.AutoNamed = false;
                RenumberAutomatic();
                try { SaveCore(); }
                catch { RestoreClips(previous, previousNumber); throw; }
            }
        }

        public void Remove(string filePath)
        {
            lock (sync)
            {
                string full = OwnPath(filePath);
                int index = clips.FindIndex(delegate(RecordingClip clip) { return SamePath(clip.FilePath, full); });
                if (index < 0) return;
                List<RecordingClip> previous = Items();
                int previousNumber = nextNumber;
                clips.RemoveAt(index);
                string fileName = Path.GetFileName(full);
                bool addedHidden = hidden.Add(fileName);
                RenumberAutomatic();
                try { SaveCore(); }
                catch
                {
                    if (addedHidden) hidden.Remove(fileName);
                    RestoreClips(previous, previousNumber);
                    throw;
                }
            }
        }

        // Commit metadata first. If deleting the file fails, restore its list entry.
        public void Delete(string filePath)
        {
            lock (sync)
            {
                string full = OwnPath(filePath);
                if (!clips.Exists(delegate(RecordingClip clip) { return SamePath(clip.FilePath, full); })) return;
                List<RecordingClip> previous = Items();
                int previousNumber = nextNumber;
                string fileName = Path.GetFileName(full);
                bool wasHidden = hidden.Contains(fileName);
                Remove(full);
                try { File.Delete(full); }
                catch (Exception failure)
                {
                    RestoreClips(previous, previousNumber);
                    if (!wasHidden) hidden.Remove(fileName);
                    try { SaveCore(true); }
                    catch (Exception rollback)
                    {
                        throw new IOException("文件未能删除，列表恢复也失败；原音频仍在录音目录，请恢复目录写入权限后重启。",
                            new AggregateException(failure, rollback));
                    }
                    throw;
                }
            }
        }

        private void SaveCore(bool preserveBackup = false)
        {
            Document doc = new Document { NextNumber = nextNumber, HiddenFiles = new List<string>(hidden) };
            foreach (RecordingClip clip in clips)
                doc.Clips.Add(new StoredClip { FileName = Path.GetFileName(clip.FilePath), Name = clip.Name, AutoNamed = clip.AutoNamed });
            string temporary = indexFile + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(doc), new UTF8Encoding(false));
                if (File.Exists(indexFile)) File.Replace(temporary, indexFile, preserveBackup ? null : indexFile + ".bak", true);
                else File.Move(temporary, indexFile);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
