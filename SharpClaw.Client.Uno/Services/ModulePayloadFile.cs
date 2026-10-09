using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FileAttributes = System.IO.FileAttributes;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Services;

internal sealed record ModulePayloadFile(string Path, long Length, string Sha256);
