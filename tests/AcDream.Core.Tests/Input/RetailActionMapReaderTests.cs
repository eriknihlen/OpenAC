using System.Collections.Generic;
using AcDream.Content;
using AcDream.Core.Content;
using AcDream.Core.Input;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DatReaderWriter.Lib.IO;
using Xunit;

namespace AcDream.Core.Tests.Input;

public sealed class RetailActionMapReaderTests
{
    [Fact]
    public void Read_FiltersNonUserBindableActions()
    {
        var actionMap = new ActionMap
        {
            StringTableId = 0x23000005u,
            InputMaps = new Dictionary<uint, Dictionary<uint, ActionMapValue>>
            {
                [4u] = new Dictionary<uint, ActionMapValue>
                {
                    [0x1u] = new ActionMapValue { UserBinding = new UserBindingData { ActionClass = 0u } },
                    [0x29u] = new ActionMapValue
                    {
                        UserBinding = new UserBindingData
                        {
                            ActionClass = 1u,
                            ActionName = 0x111u,
                            ActionDescription = 0x222u,
                        },
                    },
                },
            },
            ConflictingMaps = new Dictionary<uint, InputsConflictsValue>(),
        };

        var dats = new FakeDatObjectSource();
        dats.Add(RetailActionMapIds.ActionMapId, actionMap);

        RetailActionMapSnapshot? snapshot = RetailActionMapReader.Read(dats);

        Assert.NotNull(snapshot);
        RetailActionMapRow row = Assert.Single(snapshot!.Rows);
        Assert.Equal(4u, row.InputMapId);
        Assert.Equal(0x29u, row.ActionId);
        Assert.Equal(RetailActionClass.Movement, row.ActionClass);
        Assert.Equal(0x111u, row.LabelHash);
        Assert.Equal(0x222u, row.TooltipHash);
    }

    [Fact]
    public void Read_UnionMergesBothMasterMapsWithoutOrderDependency()
    {
        var actionMap = new ActionMap
        {
            InputMaps = new Dictionary<uint, Dictionary<uint, ActionMapValue>>
            {
                [5u] = new Dictionary<uint, ActionMapValue>
                {
                    [0x3Eu] = new ActionMapValue { UserBinding = new UserBindingData { ActionClass = 2u } },
                    [0x33u] = new ActionMapValue { UserBinding = new UserBindingData { ActionClass = 2u } },
                },
            },
            ConflictingMaps = new Dictionary<uint, InputsConflictsValue>(),
        };

        var gameplayMap = new MasterInputMap
        {
            InputMaps = new Dictionary<uint, CInputMap>
            {
                [5u] = new CInputMap
                {
                    Mappings = new List<QualifiedControl>
                    {
                        new QualifiedControl
                        {
                            Key = new ControlSpecification { Key = (0xB5u << 16) | 0u, Modifier = 0u },
                            Activation = 3u,
                            Unknown = 0x3Eu,
                        },
                    },
                },
            },
        };
        var systemMap = new MasterInputMap
        {
            InputMaps = new Dictionary<uint, CInputMap>
            {
                [5u] = new CInputMap
                {
                    Mappings = new List<QualifiedControl>
                    {
                        new QualifiedControl
                        {
                            Key = new ControlSpecification { Key = (0x4Au << 16) | 0u, Modifier = 0u },
                            Activation = 3u,
                            Unknown = 0x33u,
                        },
                    },
                },
            },
        };

        var dats = new FakeDatObjectSource();
        dats.Add(RetailActionMapIds.ActionMapId, actionMap);
        dats.Add(RetailActionMapIds.GameplayMasterMapId, gameplayMap);
        dats.Add(RetailActionMapIds.SystemMasterMapId, systemMap);

        RetailActionMapSnapshot? snapshot = RetailActionMapReader.Read(dats);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Rows.Count);

        RetailActionMapRow rowFromGameplayMap = Assert.Single(snapshot.Rows, r => r.ActionId == 0x3Eu);
        RetailKeyChord chord1 = Assert.Single(rowFromGameplayMap.DefaultBindings);
        Assert.Equal(0xB5u, chord1.Scan);

        RetailActionMapRow rowFromSystemMap = Assert.Single(snapshot.Rows, r => r.ActionId == 0x33u);
        RetailKeyChord chord2 = Assert.Single(rowFromSystemMap.DefaultBindings);
        Assert.Equal(0x4Au, chord2.Scan);
    }

    [Fact]
    public void Read_MissingActionMap_ReturnsNull()
    {
        var dats = new FakeDatObjectSource();
        Assert.Null(RetailActionMapReader.Read(dats));
    }

    [Fact]
    public void Read_MissingMasterMaps_StillReturnsRowsWithEmptyDefaults()
    {
        var actionMap = new ActionMap
        {
            InputMaps = new Dictionary<uint, Dictionary<uint, ActionMapValue>>
            {
                [4u] = new Dictionary<uint, ActionMapValue>
                {
                    [0x29u] = new ActionMapValue { UserBinding = new UserBindingData { ActionClass = 1u } },
                },
            },
            ConflictingMaps = new Dictionary<uint, InputsConflictsValue>(),
        };
        var dats = new FakeDatObjectSource();
        dats.Add(RetailActionMapIds.ActionMapId, actionMap);

        RetailActionMapSnapshot? snapshot = RetailActionMapReader.Read(dats);

        Assert.NotNull(snapshot);
        RetailActionMapRow row = Assert.Single(snapshot!.Rows);
        Assert.Empty(row.DefaultBindings);
    }

    [Fact]
    public void Read_PreservesRetailInputMapConflictPolicy()
    {
        var actionMap = new ActionMap
        {
            InputMaps = new Dictionary<uint, Dictionary<uint, ActionMapValue>>(),
            ConflictingMaps = new Dictionary<uint, InputsConflictsValue>
            {
                [0x10000003u] = new InputsConflictsValue
                {
                    InputMap = 0x10000003u,
                    ConflictingInputMaps = new List<uint>
                    {
                        0x10000003u,
                        0x10000002u,
                    },
                },
            },
        };
        var dats = new FakeDatObjectSource();
        dats.Add(RetailActionMapIds.ActionMapId, actionMap);

        RetailActionMapSnapshot snapshot = Assert.IsType<RetailActionMapSnapshot>(
            RetailActionMapReader.Read(dats));

        Assert.True(snapshot.InputMapsConflict(0x10000003u, 0x10000003u));
        Assert.True(snapshot.InputMapsConflict(0x10000003u, 0x10000002u));
        Assert.False(snapshot.InputMapsConflict(0x10000003u, 0x10000004u));
        Assert.True(snapshot.InputMapsConflict(0xDEADBEEFu, 0xDEADBEEFu));
        Assert.False(snapshot.InputMapsConflict(0xDEADBEEFu, 0x10000003u));
    }

    [Fact]
    public void RetailInputMapHeaders_HasAllNineteenByteVerifiedEntries()
    {
        Assert.Equal(19, RetailInputMapHeaders.NameByInputMapId.Count);
        Assert.Equal("ID_InputMap_MovementCommands", RetailInputMapHeaders.NameByInputMapId[0x00000004u]);
        Assert.Equal("ID_InputMap_CameraControls", RetailInputMapHeaders.NameByInputMapId[0x00000005u]);
        Assert.Equal("ID_InputMap_Emotes", RetailInputMapHeaders.NameByInputMapId[0x10000006u]);
        Assert.Equal("ID_InputMap_CharacterOptionCommands", RetailInputMapHeaders.NameByInputMapId[0x10000008u]);
        Assert.Equal("ID_InputMap_ToggleChatEntry", RetailInputMapHeaders.NameByInputMapId[0x1000000Du]);
    }

    private sealed class FakeDatObjectSource : IDatObjectSource
    {
        private readonly Dictionary<uint, object> _objects = new();

        public void Add<T>(uint fileId, T value) where T : IDBObj => _objects[fileId] = value!;

        public T? Get<T>(uint fileId) where T : IDBObj =>
            _objects.TryGetValue(fileId, out object? value) && value is T typed ? typed : default;

        public bool TryGet<T>(uint fileId, out T value) where T : IDBObj
        {
            T? found = Get<T>(fileId);
            value = found!;
            return found is not null;
        }
    }
}

[Trait("Lane", "InstalledDat")]
public sealed class RetailActionMapReader_LiveDatTests
{
    [Fact]
    public void Read_AgainstInstalledDats_MatchesPinnedShape()
    {
        string? datDir = Conformance.ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir);
        var source = new DatCollectionAdapter(dats);

        RetailActionMapSnapshot? snapshot = RetailActionMapReader.Read(source);

        Assert.NotNull(snapshot);
        Assert.Equal(306, snapshot!.Rows.Count);

        var byClass = new Dictionary<RetailActionClass, int>();
        foreach (RetailActionMapRow row in snapshot.Rows)
        {
            Assert.NotEqual(RetailActionClass.None, row.ActionClass);
            byClass.TryGetValue(row.ActionClass, out int count);
            byClass[row.ActionClass] = count + 1;
        }
        Assert.Equal(14, byClass[RetailActionClass.Movement]);
        Assert.Equal(22, byClass[RetailActionClass.Camera]);
        Assert.Equal(103, byClass[RetailActionClass.Ui]);
        Assert.Equal(32, byClass[RetailActionClass.Combat]);
        Assert.Equal(87, byClass[RetailActionClass.Emote]);
        Assert.Equal(48, byClass[RetailActionClass.CharacterSettings]);

        RetailActionMapRow moveForward = Assert.Single(
            snapshot.Rows, r => r.InputMapId == 0x4u && r.ActionId == 0x29u);
        Assert.Equal(2, moveForward.DefaultBindings.Count);
        Assert.Contains(moveForward.DefaultBindings, c => c.Scan == 0x11u); // DIK_W
        Assert.Contains(moveForward.DefaultBindings, c => c.Scan == 0xC8u); // DIK_UPARROW

        Assert.False(snapshot.InputMapsConflict(0x10000003u, 0x10000004u));
        Assert.False(snapshot.InputMapsConflict(0x10000003u, 0x10000005u));
        Assert.False(snapshot.InputMapsConflict(0x10000004u, 0x10000005u));
    }
}
