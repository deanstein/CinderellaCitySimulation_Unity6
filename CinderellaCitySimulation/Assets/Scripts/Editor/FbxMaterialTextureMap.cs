using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Reads the material-to-texture links stored inside a binary FBX.
// Unity sometimes extracts those textures into the .fbm folder and then saves the
// external material without them (or with a guid that no longer exists). The filenames
// in the FBX are the only reliable way to know which imported texture belongs on which slot.
public static class FbxMaterialTextureMap
{
    public struct MaterialTextures
    {
        public string DiffuseFileName;
        public string NormalFileName;
    }

    static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");
    static readonly Dictionary<string, Dictionary<string, MaterialTextures>> Cache =
        new Dictionary<string, Dictionary<string, MaterialTextures>>(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetMaterialTextures(string fbxPath, string materialName, out MaterialTextures textures)
    {
        textures = default;
        if (string.IsNullOrEmpty(fbxPath) || string.IsNullOrEmpty(materialName))
            return false;

        Dictionary<string, MaterialTextures> map = GetMap(fbxPath);
        if (map == null)
            return false;

        string name = materialName;
        const string instanceSuffix = " (Instance)";
        if (name.EndsWith(instanceSuffix, StringComparison.Ordinal))
            name = name.Substring(0, name.Length - instanceSuffix.Length);

        return map.TryGetValue(name, out textures);
    }

    static Dictionary<string, MaterialTextures> GetMap(string fbxPath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(fbxPath);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(fullPath))
            return null;

        DateTime writeTime;
        long length;
        try
        {
            writeTime = File.GetLastWriteTimeUtc(fullPath);
            length = new FileInfo(fullPath).Length;
        }
        catch
        {
            return null;
        }

        string cacheKey = fullPath + "|" + writeTime.Ticks + "|" + length;
        if (Cache.TryGetValue(cacheKey, out Dictionary<string, MaterialTextures> cached))
            return cached;

        Dictionary<string, MaterialTextures> map = Read(fullPath, out string error);
        if (error != null)
        {
            DebugUtils.DebugLog("<b>Could not read FBX material textures:</b> " + fbxPath + " (" + error + ")");
            return null;
        }

        // drop any previous stamp for this file so a changed FBX is not cached twice
        List<string> staleKeys = null;
        foreach (string key in Cache.Keys)
        {
            if (key.StartsWith(fullPath + "|", StringComparison.OrdinalIgnoreCase))
            {
                if (staleKeys == null)
                    staleKeys = new List<string>();
                staleKeys.Add(key);
            }
        }
        if (staleKeys != null)
        {
            foreach (string key in staleKeys)
                Cache.Remove(key);
        }

        Cache[cacheKey] = map;
        return map;
    }

    sealed class Obj
    {
        public string ClassName;
        public string Name;
    }

    static Dictionary<string, MaterialTextures> Read(string fbxPath, out string error)
    {
        error = null;
        var result = new Dictionary<string, MaterialTextures>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using (FileStream stream = File.OpenRead(fbxPath))
            {
                if (stream.Length < 27)
                {
                    error = "file too small";
                    return result;
                }

                byte[] header = new byte[27];
                if (stream.Read(header, 0, 27) != 27)
                {
                    error = "short header";
                    return result;
                }

                string magic = Encoding.ASCII.GetString(header, 0, 21);
                if (magic != "Kaydara FBX Binary  \0")
                {
                    error = "not a binary FBX";
                    return result;
                }

                uint version = BitConverter.ToUInt32(header, 23);
                bool is64 = version >= 7500;

                var objects = new Dictionary<long, Obj>();
                var fileNames = new Dictionary<long, string>();
                var ooChildren = new Dictionary<long, List<long>>();
                var opLinks = new List<OpLink>();

                while (stream.Position + HeaderSize(is64) <= stream.Length)
                {
                    long nodeStart = stream.Position;
                    if (!TryReadHeader(stream, is64, out long endOffset, out _, out _, out string name))
                        break;
                    if (endOffset == 0)
                        break;

                    if (name == "Objects")
                        ReadObjects(stream, is64, endOffset, objects, fileNames);
                    else if (name == "Connections")
                        ReadConnections(stream, is64, endOffset, ooChildren, opLinks);
                    else
                        stream.Seek(endOffset, SeekOrigin.Begin);

                    if (stream.Position < endOffset && endOffset > nodeStart)
                        stream.Seek(endOffset, SeekOrigin.Begin);
                    if (stream.Position == nodeStart)
                        break;
                }

                foreach (OpLink link in opLinks)
                {
                    Obj parent;
                    if (!objects.TryGetValue(link.Parent, out parent))
                        continue;
                    if (parent.ClassName != "Material" || string.IsNullOrEmpty(parent.Name))
                        continue;

                    string fileName = ResolveFileName(link.Child, fileNames, ooChildren, new HashSet<long>());
                    if (string.IsNullOrEmpty(fileName))
                        continue;

                    MaterialTextures textures;
                    if (!result.TryGetValue(parent.Name, out textures))
                        textures = new MaterialTextures();

                    if (IsDiffuseProperty(link.Property))
                    {
                        if (textures.DiffuseFileName == null)
                            textures.DiffuseFileName = fileName;
                    }
                    else if (IsNormalProperty(link.Property))
                    {
                        if (textures.NormalFileName == null)
                            textures.NormalFileName = fileName;
                    }

                    result[parent.Name] = textures;
                }
            }
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
        }

        return result;
    }

    struct OpLink
    {
        public long Child;
        public long Parent;
        public string Property;
    }

    static int HeaderSize(bool is64)
    {
        return is64 ? 25 : 13;
    }

    static bool TryReadHeader(Stream stream, bool is64, out long endOffset, out long numProperties, out long propertyListLen, out string name)
    {
        endOffset = 0;
        numProperties = 0;
        propertyListLen = 0;
        name = "";

        int size = HeaderSize(is64);
        byte[] buf = new byte[size];
        int read = stream.Read(buf, 0, size);
        if (read != size)
            return false;

        int nameLen;
        if (is64)
        {
            endOffset = BitConverter.ToInt64(buf, 0);
            numProperties = BitConverter.ToInt64(buf, 8);
            propertyListLen = BitConverter.ToInt64(buf, 16);
            nameLen = buf[24];
        }
        else
        {
            endOffset = BitConverter.ToUInt32(buf, 0);
            numProperties = BitConverter.ToUInt32(buf, 4);
            propertyListLen = BitConverter.ToUInt32(buf, 8);
            nameLen = buf[12];
        }

        if (nameLen > 0)
        {
            byte[] nameBytes = new byte[nameLen];
            if (stream.Read(nameBytes, 0, nameLen) != nameLen)
                return false;
            name = Encoding.ASCII.GetString(nameBytes);
        }

        return true;
    }

    static void ReadObjects(Stream stream, bool is64, long endOffset, Dictionary<long, Obj> objects, Dictionary<long, string> fileNames)
    {
        while (stream.Position < endOffset)
        {
            long start = stream.Position;
            long childEnd;
            long numProps;
            string name;
            if (!TryReadHeader(stream, is64, out childEnd, out numProps, out _, out name))
                break;
            if (childEnd == 0)
                break;

            bool interesting = name == "Material" || name == "Texture" || name == "Video";
            if (!interesting)
            {
                stream.Seek(childEnd, SeekOrigin.Begin);
                if (stream.Position == start)
                    break;
                continue;
            }

            List<object> props = ReadProperties(stream, numProps);
            long id = 0;
            bool hasId = false;
            if (props.Count > 0 && props[0] is long)
            {
                id = (long)props[0];
                hasId = true;
            }
            else if (props.Count > 0 && props[0] is int)
            {
                id = (int)props[0];
                hasId = true;
            }

            if (hasId)
            {
                string objName = props.Count > 1 && props[1] is string ? CleanName((string)props[1]) : "";
                objects[id] = new Obj { ClassName = name, Name = objName };
            }

            while (stream.Position < childEnd)
            {
                long nestedStart = stream.Position;
                long nestedEnd;
                long nestedCount;
                string nestedName;
                if (!TryReadHeader(stream, is64, out nestedEnd, out nestedCount, out _, out nestedName))
                    break;
                if (nestedEnd == 0)
                    break;

                if (hasId && IsFileNameNode(nestedName))
                {
                    List<object> nestedProps = ReadProperties(stream, nestedCount);
                    string fileName = FirstImageFileName(nestedProps);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        // an absolute FileName is more trustworthy than a relative one that follows it
                        bool isRelative = nestedName.StartsWith("Relative", StringComparison.OrdinalIgnoreCase);
                        if (!fileNames.ContainsKey(id) || !isRelative)
                            fileNames[id] = fileName;
                    }
                    SkipTo(stream, nestedEnd);
                }
                else if (hasId && nestedName == "Properties70")
                {
                    CaptureProperties70FileName(stream, is64, nestedEnd, id, fileNames);
                }
                else
                {
                    stream.Seek(nestedEnd, SeekOrigin.Begin);
                }

                if (stream.Position == nestedStart)
                    break;
            }

            if (stream.Position < childEnd)
                stream.Seek(childEnd, SeekOrigin.Begin);
            if (stream.Position == start)
                break;
        }
    }

    static void CaptureProperties70FileName(Stream stream, bool is64, long endOffset, long objectId, Dictionary<long, string> fileNames)
    {
        while (stream.Position < endOffset)
        {
            long start = stream.Position;
            long childEnd;
            long numProps;
            string name;
            if (!TryReadHeader(stream, is64, out childEnd, out numProps, out _, out name))
                break;
            if (childEnd == 0)
                break;

            if (name == "P")
            {
                List<object> props = ReadProperties(stream, numProps);
                if (props.Count > 0 && props[0] is string && IsFileNameNode((string)props[0]) && !fileNames.ContainsKey(objectId))
                {
                    string fileName = FirstImageFileName(props);
                    if (!string.IsNullOrEmpty(fileName))
                        fileNames[objectId] = fileName;
                }
                SkipTo(stream, childEnd);
            }
            else
            {
                stream.Seek(childEnd, SeekOrigin.Begin);
            }

            if (stream.Position == start)
                break;
        }

        SkipTo(stream, endOffset);
    }

    static void ReadConnections(Stream stream, bool is64, long endOffset, Dictionary<long, List<long>> ooChildren, List<OpLink> opLinks)
    {
        while (stream.Position < endOffset)
        {
            long start = stream.Position;
            long childEnd;
            long numProps;
            string name;
            if (!TryReadHeader(stream, is64, out childEnd, out numProps, out _, out name))
                break;
            if (childEnd == 0)
                break;

            if (name == "C")
            {
                List<object> props = ReadProperties(stream, numProps);
                string type = props.Count > 0 && props[0] is string ? (string)props[0] : "";
                long child = AsLong(props, 1);
                long parent = AsLong(props, 2);
                string prop = props.Count > 3 && props[3] is string ? (string)props[3] : "";

                if (type == "OO")
                {
                    List<long> list;
                    if (!ooChildren.TryGetValue(parent, out list))
                    {
                        list = new List<long>();
                        ooChildren[parent] = list;
                    }
                    list.Add(child);
                }
                else if (type == "OP" && (IsDiffuseProperty(prop) || IsNormalProperty(prop)))
                {
                    opLinks.Add(new OpLink { Child = child, Parent = parent, Property = prop });
                }

                SkipTo(stream, childEnd);
            }
            else
            {
                stream.Seek(childEnd, SeekOrigin.Begin);
            }

            if (stream.Position == start)
                break;
        }
    }

    static List<object> ReadProperties(Stream stream, long count)
    {
        var props = new List<object>();
        for (long i = 0; i < count; i++)
            props.Add(ReadProperty(stream));
        return props;
    }

    static object ReadProperty(Stream stream)
    {
        int type = stream.ReadByte();
        if (type < 0)
            throw new EndOfStreamException("property type");

        switch (type)
        {
            case 'Y':
                return ReadInt16(stream);
            case 'C':
                return stream.ReadByte() != 0;
            case 'I':
                return ReadInt32(stream);
            case 'F':
                Skip(stream, 4);
                return null;
            case 'D':
                Skip(stream, 8);
                return null;
            case 'L':
                return ReadInt64(stream);
            case 'S':
                return ReadByteString(stream);
            case 'R':
                Skip(stream, ReadUInt32(stream));
                return null;
            case 'f':
            case 'd':
            case 'l':
            case 'i':
            case 'b':
                Skip(stream, 4);
                Skip(stream, 4);
                Skip(stream, ReadUInt32(stream));
                return null;
            default:
                throw new InvalidDataException("unknown FBX property type " + type + " at " + stream.Position);
        }
    }

    static string ReadByteString(Stream stream)
    {
        uint length = ReadUInt32(stream);
        if (length == 0)
            return "";
        if (length > 8192)
        {
            Skip(stream, length);
            return "";
        }

        byte[] bytes = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = stream.Read(bytes, read, (int)length - read);
            if (n <= 0)
                break;
            read += n;
        }
        return Latin1.GetString(bytes, 0, read);
    }

    static bool IsFileNameNode(string name)
    {
        return name.Equals("FileName", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Filename", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RelativeFilename", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RelativeFileName", StringComparison.OrdinalIgnoreCase);
    }

    static string FirstImageFileName(List<object> props)
    {
        for (int i = props.Count - 1; i >= 0; i--)
        {
            if (!(props[i] is string))
                continue;

            string value = ((string)props[i]).Trim();
            if (value.Length == 0)
                continue;

            string file = Path.GetFileName(value.Replace('\\', '/'));
            if (string.IsNullOrEmpty(file))
                continue;

            string ext = Path.GetExtension(file);
            if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".tga", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".psd", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".exr", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".hdr", StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    static bool IsDiffuseProperty(string prop)
    {
        string leaf = Leaf(prop);
        return leaf.Equals("DiffuseColor", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Diffuse", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("baseColor", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("BaseColor", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("texmap_diffuse", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsNormalProperty(string prop)
    {
        string leaf = Leaf(prop);
        return leaf.Equals("NormalMap", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Bump", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("normalCamera", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("texmap_bump", StringComparison.OrdinalIgnoreCase);
    }

    static string Leaf(string prop)
    {
        if (string.IsNullOrEmpty(prop))
            return "";
        int bar = prop.LastIndexOf('|');
        return bar >= 0 ? prop.Substring(bar + 1) : prop;
    }

    static string ResolveFileName(long id, Dictionary<long, string> fileNames, Dictionary<long, List<long>> ooChildren, HashSet<long> visited)
    {
        if (!visited.Add(id))
            return null;
        string file;
        if (fileNames.TryGetValue(id, out file) && !string.IsNullOrEmpty(file))
            return file;

        List<long> children;
        if (!ooChildren.TryGetValue(id, out children))
            return null;

        foreach (long child in children)
        {
            string childFile = ResolveFileName(child, fileNames, ooChildren, visited);
            if (!string.IsNullOrEmpty(childFile))
                return childFile;
        }
        return null;
    }

    static string CleanName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";
        int nul = raw.IndexOf('\0');
        if (nul >= 0)
            raw = raw.Substring(0, nul);
        int sep = raw.LastIndexOf("::", StringComparison.Ordinal);
        if (sep >= 0)
            raw = raw.Substring(sep + 2);
        return raw.Trim();
    }

    static long AsLong(List<object> props, int index)
    {
        if (index >= props.Count || props[index] == null)
            return 0;
        if (props[index] is long)
            return (long)props[index];
        if (props[index] is int)
            return (int)props[index];
        return 0;
    }

    static void SkipTo(Stream stream, long position)
    {
        if (stream.Position < position)
            stream.Seek(position, SeekOrigin.Begin);
    }

    static void Skip(Stream stream, long count)
    {
        if (count <= 0)
            return;
        stream.Seek(count, SeekOrigin.Current);
    }

    static short ReadInt16(Stream stream)
    {
        byte[] buf = new byte[2];
        ReadExact(stream, buf);
        return BitConverter.ToInt16(buf, 0);
    }

    static int ReadInt32(Stream stream)
    {
        byte[] buf = new byte[4];
        ReadExact(stream, buf);
        return BitConverter.ToInt32(buf, 0);
    }

    static long ReadInt64(Stream stream)
    {
        byte[] buf = new byte[8];
        ReadExact(stream, buf);
        return BitConverter.ToInt64(buf, 0);
    }

    static uint ReadUInt32(Stream stream)
    {
        byte[] buf = new byte[4];
        ReadExact(stream, buf);
        return BitConverter.ToUInt32(buf, 0);
    }

    static void ReadExact(Stream stream, byte[] buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = stream.Read(buf, read, buf.Length - read);
            if (n <= 0)
                throw new EndOfStreamException();
            read += n;
        }
    }
}
