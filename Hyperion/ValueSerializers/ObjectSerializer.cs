#region copyright
// -----------------------------------------------------------------------
//  <copyright file="ObjectSerializer.cs" company="Akka.NET Team">
//      Copyright (C) 2015-2016 AsynkronIT <https://github.com/AsynkronIT>
//      Copyright (C) 2016-2016 Akka.NET Team <https://github.com/akkadotnet>
//  </copyright>
// -----------------------------------------------------------------------
#endregion

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Hyperion.Extensions;

namespace Hyperion.ValueSerializers
{
    public class ObjectSerializer : ValueSerializer
    {
        public const byte ManifestVersion = 251;
        public const byte ManifestFull = 255;
        public const byte ManifestIndex = 254;

        private readonly byte[] _manifest;
        private readonly byte[] _manifestWithVersionInfo;

        private volatile bool _isInitialized;
        private ObjectReader _reader;
        private ObjectWriter _writer;
        int _preallocatedBufferSize;

        public ObjectSerializer(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            Type = type;
            var typeName = type.GetShortAssemblyQualifiedName();
            // ReSharper disable once PossibleNullReferenceException
            // ReSharper disable once AssignNullToNotNullAttribute
            var typeNameBytes = typeName.ToUtf8Bytes();

            var fields = type.GetFieldInfosForType();
            var fieldNames = fields.Select(field => field.Name.ToUtf8Bytes()).ToList();
            var versionInfo = TypeEx.GetTypeManifest(fieldNames);

            //precalculate the entire manifest for this serializer
            //this helps us to minimize calls to Stream.Write/WriteByte 
            _manifest =
                new[] {ManifestFull}
                    .Concat(BitConverter.GetBytes(typeNameBytes.Length))
                    .Concat(typeNameBytes)
                    .ToArray(); //serializer id 255 + assembly qualified name

            //TODO: this should only work this way for standard poco objects
            //custom object serializers should not emit their inner fields

            //this is the same as the above, but including all field names of the type, in alphabetical order
            _manifestWithVersionInfo =
                new[] {ManifestVersion}
                    .Concat(BitConverter.GetBytes(typeNameBytes.Length))
                    .Concat(typeNameBytes)
                    .Concat(versionInfo)
                    .ToArray(); //serializer id 255 + assembly qualified name + versionInfo

            //initialize reader and writer with dummy handlers that wait until the serializer is fully initialized
            _writer = (stream, o, session) =>
            {
                SpinWait.SpinUntil(() => _isInitialized);
                WriteValue(stream, o, session);
            };

            _reader = (stream, session) =>
            {
                SpinWait.SpinUntil(() => _isInitialized);
                return ReadValue(stream, session);
            };
        }

        public Type Type { get; }

        public override void WriteManifest(Stream stream, SerializerSession session)
        {
            ushort typeIdentifier;
            if (session.ShouldWriteTypeManifest(Type, out typeIdentifier))
            {
                session.TrackSerializedType(Type);

                var manifestToWrite = session.Serializer.Options.VersionTolerance
                    ? _manifestWithVersionInfo
                    : _manifest;

                stream.Write(manifestToWrite);
            }
            else
            {
                stream.WriteByte(ManifestIndex);
                UInt16Serializer.WriteValueImpl(stream, typeIdentifier, session);
            }
        }

        public override void WriteValue(Stream stream, object value, SerializerSession session)
            => _writer(stream, value, session);

        public override object ReadValue(Stream stream, DeserializerSession session) => _reader(stream, session);

        public override Type GetElementType() => Type;

        public void Initialize(ObjectReader reader, ObjectWriter writer, int preallocatedBufferSize = 0)
        {
            _preallocatedBufferSize = preallocatedBufferSize;
            _reader = reader;
            _writer = writer;
            _isInitialized = true;
        }

        public override int PreallocatedByteBufferSize => _preallocatedBufferSize;
    }
}