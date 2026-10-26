#if KALYX_LANG
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Atsm.Abstractions.Diagnostics;
using Atsm.Diagnostics;
using Atsm.Plugin.Sysmex.Urinalysis;

namespace Atsm.Tests.Diagnostics;

public sealed class LangIntegrationTests
{
    [Fact]
    public async Task Real_Lang_runtime_applies_host_translations_and_registers_each_plugin_seed_without_owning_the_connection()
    {
        using var database = new TranslationConnection();
        database.Translations[("Atsm.Diagnostics.Core", "Finding.ATSM-DIAG-INCOMPLETE-CASE")] = "CASO TRADUCIDO";
        database.Translations[("Atsm.Plugin.Sysmex.Urinalysis", "record.S")] = "CONTROL TRADUCIDO";
        database.Translations[("Atsm.Plugin.Sysmex.Urinalysis", "finding.ATSM-UC3500-ER04")] = "TIRA TRADUCIDA";
        database.Translations[("Atsm.Plugin.Sysmex.Urinalysis", "value.Crosses")] = "CRUCES TRADUCIDAS";
        database.Translations[("Atsm.Plugin.Sysmex.Urinalysis", "flag.N")] = "ESTADO TRADUCIDO";
        database.Translations[("Atsm.Plugin.Sysmex.Urinalysis", "value.Image")] = "IMAGEN TRADUCIDA";
        database.Translations[("Atsm.Plugin.Sysmex.Urinalysis", "finding.ATSM-UWAM-CHEMISTRY-UC3500-9902")] = "ERROR TRADUCIDO";
        database.Translations[("Atsm.Plugin.Sysmex.Urinalysis", "flag.UC3500.00CC")] = "ESTADO DE ERROR TRADUCIDO";
        database.Translations[("Atsm.Plugin.Sysmex.Urinalysis", "unit.NotTransmitted")] = "UNIDAD NO INFORMADA TRADUCIDA";
        database.Translations[("Atsm.Plugin.Sysmex.Cs2500", "evaluation.0000.4000.0030")] = "CALIBRACION TRADUCIDA";
        database.Translations[("Atsm.Plugin.Sysmex.XnL", "ip.PLT_Clumps?")] = "SOSPECHA TRADUCIDA";
        database.Translations[("Atsm.Plugin.Diagast.Qwalys", "reaction.A.CMP")] = "COMPATIBILIDAD TRADUCIDA";
        database.Translations[("Atsm.Plugin.Sysmex.Urinalysis", "research.BACT-Info..3")] = "INDICADOR TRADUCIDO";
        database.Translations[("Atsm.Plugin.Sysmex.Urinalysis", "uf.need.DBS_LANG_02215")] = "LIMPIEZA TRADUCIDA";
        var catalog = new DiagnosticCatalog(); await DiagnosticText.InitializeAsync(database, catalog);
        Assert.Equal("CASO TRADUCIDO", DiagnosticText.Finding(new("ATSM-DIAG-INCOMPLETE-CASE", AtsmSeverity.Warning, "Unchanged original")));
        Assert.Equal("CONTROL TRADUCIDO", ProtocolExplainer.RecordNames("S|1\r", catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile)));
        Assert.Contains(database.Missing, label => label.Source == "Atsm.Diagnostics.Core");
        Assert.Contains(database.Missing, label => label.Source == "Atsm.Plugin.Abbott.Architect");
        Assert.Contains(database.Missing, label => label.Source == "Atsm.Plugin.Sysmex.Cs2500");
        Assert.Contains(database.Missing, label => label.Source == "Atsm.Plugin.Sysmex.Urinalysis");
        Assert.Equal("TIRA TRADUCIDA", DiagnosticText.Finding(new("ATSM-UC3500-ER04", AtsmSeverity.Warning, "Original"), catalog.Resolve("sysmex.uc3500.serial")));
        var plugin = catalog.Resolve("sysmex.uc3500.serial").TracePlugin!;
        Assert.Equal("CRUCES TRADUCIDAS", DiagnosticText.Plugin(plugin, plugin.DiagnosticVocabulary["value.Crosses"]));
        var profile = catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var input = DiagnosticReader.Read("H|\\^&|||U-WAM\rP|1\rO|1|SYN\rR|1|^^^IMAGE^A^1^IF|SYN.png^RAW|||N|||||||UF-5000\rL|1\r"u8.ToArray(), DiagnosticInputFormat.AstmMessage, profile, "synthetic", TimeSpan.Zero);
        var observation = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc)).Observations.Single();
        Assert.Equal("ESTADO TRADUCIDO", ProtocolExplainer.Status(observation, profile));
        Assert.Equal("IMAGEN TRADUCIDA", ProtocolExplainer.ResultKind(observation, profile));
        Assert.Equal("IMAGEN TRADUCIDA", ProtocolExplainer.ResultValue(observation, profile));
        Assert.Equal("ERROR TRADUCIDO", DiagnosticText.Finding(new("ATSM-UWAM-CHEMISTRY-UC3500-9902", AtsmSeverity.Warning, "Original"), profile));
        var direct = observation with { Test = Atsm.Abstractions.Model.CodeSet.Of(new("BLD", null, "urn:atsm:vendor:sysmex.uc3500")),
            ResultKind = "CONCENTRATION", Flags = [new("00CC", null, "urn:atsm:vendor:sysmex.uc3500")], InstrumentStatusMeaning = Atsm.Abstractions.Model.InstrumentStatusMeaning.Warning };
        var directProfile = catalog.Resolve(SysmexUrinalysisPlugin.Uc3500Profile);
        Assert.Equal("ESTADO DE ERROR TRADUCIDO", ProtocolExplainer.Status(direct, directProfile));
        Assert.Equal("UNIDAD NO INFORMADA TRADUCIDA", ProtocolExplainer.Unit(direct, directProfile));
        var cs = observation with { Test = Atsm.Abstractions.Model.CodeSet.Of(new("PT", null, "urn:atsm:vendor:sysmex.cs2500")),
            Flags = [new("0000.4000.0030", null, "urn:atsm:vendor:sysmex.cs2500:evaluation-error")] };
        Assert.Equal("CALIBRACION TRADUCIDA", ProtocolExplainer.Status(cs, catalog.Resolve("sysmex.cs2500.astm02.results")));
        var xn = observation with { Test = Atsm.Abstractions.Model.CodeSet.Of(new("PLT_Clumps?", null, "urn:atsm:vendor:sysmex.xnl")),
            Flags = [new("A", null, "urn:atsm:vendor:sysmex.xnl:flag")] };
        Assert.Equal("SOSPECHA TRADUCIDA", ProtocolExplainer.Status(xn, catalog.Resolve("sysmex.xnl.astm02.results")));
        var qw = observation with { Test = Atsm.Abstractions.Model.CodeSet.Of(new("SYN", null, "urn:atsm:vendor:diagast.qwalys3")),
            ResultKind = "A", Value = new("^^^^^^CMP", Coded: new("CMP", null, "urn:synthetic")) };
        Assert.Equal("COMPATIBILIDAD TRADUCIDA", ProtocolExplainer.ResultValue(qw, catalog.Resolve("diagast.qwalys3.astm.results")));
        Assert.Equal("^^^^^^CMP", qw.Value.RawText);
        var research = observation with { Test = Atsm.Abstractions.Model.CodeSet.Of(new("BACT-Info.", null, "urn:atsm:vendor:sysmex.uwam")),
            Instrument = observation.Instrument with { Model = "U-WAM" }, VendorIdentity = new(new Dictionary<string, string> { ["instrument.id"] = "UF-5000" }),
            ResultKind = "S/RAW", Value = new("3", Coded: new("3", null, "urn:synthetic")) };
        Assert.Equal("INDICADOR TRADUCIDO", ProtocolExplainer.ResultValue(research, profile));
        var ufNeed = observation with { Test = Atsm.Abstractions.Model.CodeSet.Of(new("DBS_LANG_02215", null, "urn:atsm:vendor:sysmex.uf.control")), Value = new("Necesidad") };
        Assert.Equal("LIMPIEZA TRADUCIDA", ProtocolExplainer.ResultValue(ufNeed, catalog.Resolve(SysmexUrinalysisPlugin.UfControlProfile)));
        Assert.Equal("Necesidad", ufNeed.Value.RawText);
        Assert.Equal(ConnectionState.Open, database.State);
    }

    // An already-open host connection. This exercises the real Lang API without installing another SQLite provider.
    private sealed class TranslationConnection : DbConnection
    {
        public Dictionary<(string Source, string Label), string> Translations { get; } = [];
        public HashSet<(string Source, string Label)> Missing { get; } = [];
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "synthetic-translations";
        public override string DataSource => "memory";
        public override string ServerVersion => "test";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => throw new InvalidOperationException("Borrowed connection must remain open.");
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new TranslationCommand(this);
        protected override void Dispose(bool disposing) { }
    }
    private sealed class TranslationCommand(TranslationConnection connection) : DbCommand
    {
        private readonly ParameterCollection _parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new Parameter();
        public override int ExecuteNonQuery()
        {
            if (CommandText.StartsWith("INSERT", StringComparison.Ordinal)) connection.Missing.Add((Value("@source"), Value("@label")));
            return 1;
        }
        public override object? ExecuteScalar() => throw new NotSupportedException();
        private string Value(string name) => Convert.ToString(_parameters[name].Value, System.Globalization.CultureInfo.InvariantCulture)!;
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var table = new DataTable(); table.Columns.Add("Label", typeof(string)); table.Columns.Add("Value", typeof(string));
            foreach (var pair in connection.Translations.Where(p => p.Key.Source == Value("@source"))) table.Rows.Add(pair.Key.Label, pair.Value);
            return table.CreateDataReader();
        }
    }
    private sealed class Parameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = "";
        [AllowNull] public override string SourceColumn { get; set; } = "";
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }
    private sealed class ParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _items.Add((DbParameter)value); return Count - 1; }
        public override void AddRange(Array values) { foreach (var value in values) Add(value!); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string name) => _items.FindIndex(p => p.ParameterName == name);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string name) => RemoveAt(IndexOf(name));
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string name) => _items[IndexOf(name)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string name, DbParameter value) => _items[IndexOf(name)] = value;
    }
}
#endif
