# AampSharp

A C# reader and writer for **AAMP**, the parameter archive format *Breath of the Wild* uses for
actor parameters (`.bxml`, `.baiprog`, `.bphysics`, `.bas`, `.bumii` and many more).

```csharp
ParameterIO pio = ParameterIO.FromBinary(bytes);

ParameterObject info = pio.Root.Object("ControllerInfo")!;
info.Set("BaseScale", Parameter.FromVector3(new(2, 2, 2)));

foreach (var (hash, list) in pio.Root.Lists)
    Console.WriteLine(NameTable.BotW.NameOf(hash));

byte[] rebuilt = pio.ToBinary();
```

## Build

```bash
dotnet build AampSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
