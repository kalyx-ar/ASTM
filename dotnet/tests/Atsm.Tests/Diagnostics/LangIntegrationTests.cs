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
        var catalog = new DiagnosticCatalog(); await DiagnosticText.InitializeAsync(database, catalog);
        Assert.Equal("CASO TRADUCIDO", DiagnosticText.Finding(new("ATSM-DIAG-INCOMPLETE-CASE", AtsmSeverity.Warning, "Unchanged original")));
        Assert.Equal("CONTROL TRADUCIDO", ProtocolExplainer.RecordNames("S|1\r", catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile)));
        Assert.Contains(database.Missing, label => label.Source == "Atsm.Diagnostics.Core");
        Assert.Contains(database.Missing, label => label.Source == "Atsm.Plugin.Abbott.Architect");
        Assert.Contains(database.Missing, label => label.Source == "Atsm.Plugin.Sysmex.Cs2500");
        Assert.Contains(database.Missing, label => label.Source == "Atsm.Plugin.Sysmex.Urinalysis");
        Assert.Equal("TIRA TRADUCIDA", DiagnosticText.Finding(new("ATSM-UC3500-ER04", AtsmSeverity.Warning, "Original"), catalog.Resolve("sysmex.uc3500.serial")));
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
