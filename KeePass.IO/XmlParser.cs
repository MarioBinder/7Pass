using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using KeePass.IO.Utils;

namespace KeePass.IO
{
    internal class XmlParser
    {
        private readonly CryptoRandomStream _crypto;
        private readonly Dispatcher _dispatcher;
        private readonly Stream _stream;

        public XmlParser(CryptoRandomStream crypto,
            Stream stream, Dispatcher dispatcher)
        {
            if (crypto == null) throw new ArgumentNullException("crypto");
            if (stream == null) throw new ArgumentNullException("stream");
            if (dispatcher == null) throw new ArgumentNullException("dispatcher");

            _crypto = crypto;
            _stream = stream;
            _dispatcher = dispatcher;
        }
        
        public Database Parse()
        {
            var settings = new XmlReaderSettings
            {
                CloseInput = false,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                IgnoreProcessingInstructions = true,
            };

            using (var reader = XmlReader.Create(_stream, settings))
            {
                if (!reader.ReadToFollowing("KeePassFile"))
                    return null;

                if (!reader.ReadToDescendant("Meta"))
                    return null;

                string recycleBinId = null;
                var icons = new Dictionary<string, ImageSource>();
                using (var subReader = reader.ReadSubtree())
                {
                    subReader.ReadToFollowing("Generator");

                    while (!string.IsNullOrEmpty(subReader.Name))
                    {
                        subReader.Skip();

                        switch (subReader.Name)
                        {
                            case "RecycleBinUUID":
                                recycleBinId = subReader
                                    .ReadElementContentAsString();
                                break;

                            case "CustomIcons":
                                ParseIcons(subReader, _dispatcher, icons);
                                break;
                        }
                    }
                }

                if (reader.Name != "Root" &&
                    !reader.ReadToNextSibling("Root"))
                {
                    return null;
                }

                if (!reader.ReadToDescendant("Group"))
                    return null;

                using (var subReader = reader.ReadSubtree())
                {
                    var root = ParseGroup(subReader);
                    var database = new Database(root, icons);

                    if (!string.IsNullOrEmpty(recycleBinId))
                    {
                        database.RecycleBin = database
                            .GetGroup(recycleBinId);
                    }

                    return database;
                }
            }
        }

        private static bool IsProtected(XmlReader reader)
        {
            if (!reader.HasAttributes)
                return false;

            if (!reader.MoveToAttribute("Protected"))
                return false;

            return reader.Value == "True";
        }

        private void ParseChildren(XmlReader reader, Group group)
        {
            while (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "Group":
                        using (var subReader = reader.ReadSubtree())
                            group.Add(ParseGroup(subReader));
                        reader.ReadEndElement();
                        break;

                    case "Entry":
                        using (var subReader = reader.ReadSubtree())
                            group.Add(ParseEntry(subReader));
                        reader.ReadEndElement();
                        break;

                    default:
                        reader.Skip();
                        break;
                }
            }

            group.Sort();
        }

        private Entry ParseEntry(XmlReader reader)
        {
            var id = ReadId(reader);
            var icon = ParseIcon(reader);

            var lastModified = ReadLastModified(reader);
            var fields = ReadFields(reader);

            if (fields.Count == 0)
                return null;

            var histories = ReadHistories(reader);
            var entry = new Entry(fields)
            {
                ID = id,
                Icon = icon,
                Histories = histories,
                LastModified = lastModified,
            };

            return entry;
        }

        private Group ParseGroup(XmlReader reader)
        {
            var id = ReadId(reader);

            if (reader.Name != "Name")
                reader.ReadToNextSibling("Name");

            var name = reader
                .ReadElementContentAsString();

            var icon = ParseIcon(reader);

            var group = new Group
            {
                ID = id,
                Name = name,
                Icon = icon,
            };

            ParseChildren(reader, group);

            return group;
        }

        private static IconData ParseIcon(XmlReader reader)
        {
            var data = new IconData();

            if (reader.Name != "IconID")
                reader.ReadToNextSibling("IconID");

            data.Standard = reader
                .ReadElementContentAsInt();

            if (reader.Name == "CustomIconUUID")
            {
                data.Custom = reader
                    .ReadElementContentAsString();
            }

            return data;
        }

        private static void ParseIcons(
            XmlReader reader, Dispatcher dispatcher,
            IDictionary<string, ImageSource> icons)
        {
            while (reader.ReadToFollowing("UUID"))
            {
                var id = reader.ReadElementContentAsString();

                if (reader.Name != "Data" &&
                    !reader.ReadToNextSibling("Data"))
                {
                    continue;
                }

                var data = Convert.FromBase64String(
                    reader.ReadElementContentAsString());

                BitmapImage image = null;

                if (dispatcher.CheckAccess())
                {
                    image = new BitmapImage();
                    image.SetSource(new MemoryStream(data));
                }
                else
                {
                    var wait = new ManualResetEvent(false);

                    dispatcher.BeginInvoke(() =>
                    {
                        image = new BitmapImage();
                        image.SetSource(new MemoryStream(data));

                        wait.Set();
                    });

                    wait.WaitOne();
                }
                
                icons.Add(id, image);
            }
        }

        private Dictionary<string, string>
            ReadFields(XmlReader reader)
        {
            var fields = new Dictionary<string, string>();

            if (reader.ReadToFollowing("String"))
            {
                while (reader.Name == "String")
                {
                    reader.Read();

                    var name = reader.ReadElementContentAsString();
                    fields.Add(name, ReadValue(reader));

                    reader.ReadEndElement();
                }
            }

            return fields;
        }

        private List<Entry> ReadHistories(XmlReader reader)
        {
            var histories = new List<Entry>();

            if (reader.ReadToFollowing("History"))
            {
                if (reader.ReadToDescendant("Entry"))
                {
                    while (reader.Name == "Entry")
                    {
                        using (var subReader = reader.ReadSubtree())
                        {
                            var history = ParseEntry(subReader);
                            if (history != null)
                                histories.Add(history);
                        }

                        reader.ReadEndElement();
                    }
                }
            }

            return histories;
        }

        private static string ReadId(XmlReader reader)
        {
            reader.ReadToDescendant("UUID");
            return reader.ReadElementContentAsString();
        }

        private static DateTime ReadLastModified(XmlReader reader)
        {
            if (reader.ReadToFollowing("Times"))
            {
                using (var subReader = reader.ReadSubtree())
                {
                    if (subReader.ReadToDescendant("LastModificationTime"))
                        return subReader.ReadElementContentAsDateTime();
                }
            }

            return DateTime.MinValue;
        }

        private string ReadValue(XmlReader reader)
        {
            var isProtected = IsProtected(reader);
            reader.MoveToContent();
            var value = reader.ReadElementContentAsString();

            if (isProtected && !string.IsNullOrEmpty(value))
            {
                var encrypted = Convert
                    .FromBase64String(value);
                var pad = _crypto.GetRandomBytes(
                    (uint)encrypted.Length);

                for (var i = 0; i < encrypted.Length; i++)
                    encrypted[i] ^= pad[i];

                value = Encoding.UTF8.GetString(
                    encrypted, 0, encrypted.Length);
            }

            return value;
        }
    }
}