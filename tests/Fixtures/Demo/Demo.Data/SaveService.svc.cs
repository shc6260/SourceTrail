using System;
using System.CodeDom.Compiler;
using System.Data;
using System.Data.Common;
using System.ServiceModel;

namespace System.ServiceModel
{
    internal sealed class ServiceContractAttribute : Attribute { }
    internal sealed class OperationContractAttribute : Attribute
    {
        public string Action { get; set; }
    }
    internal abstract class ClientBase<TChannel>
    {
        protected TChannel Channel => throw new NotSupportedException();
    }
}

namespace Demo.Data
{
    [ServiceContract]
    internal interface ISave
    {
        [OperationContract]
        void Save(DataTable rows);
    }

    internal sealed class SaveService : ISave
    {
        public void Save(DataTable rows) => new GuardedSaveAdapter().Update(rows);
    }

    internal static class SaveClientEntry
    {
        public static void Save(DataTable rows) => new SaveClient().Save(rows);
    }

    [GeneratedCode("System.ServiceModel", "4.0.0.0")]
    internal sealed class SaveClient : ClientBase<Proxy.ISave>, Proxy.ISave
    {
        public void Save(DataTable rows) => ((Proxy.ISave)this).Save(rows);
        void Proxy.ISave.Save(DataTable rows) => Channel.Save(rows);
    }

    internal class SaveAdapter
    {
        private sealed class FixtureAdapter : DbDataAdapter
        {
            public new DbCommand UpdateCommand { get; set; }
            public new DbCommand InsertCommand { get; set; }
        }

        private FixtureAdapter _adapter;
        private FixtureAdapter Adapter
        {
            get
            {
                if (_adapter == null) InitAdapter();
                return _adapter;
            }
        }

        private void InitAdapter()
        {
            _adapter = new FixtureAdapter();
            _adapter.UpdateCommand = DbProviderFactories.GetFactory("fixture").CreateCommand();
            _adapter.UpdateCommand.CommandText = "dbo.usp_SaveFixture";
            _adapter.UpdateCommand.CommandType = CommandType.StoredProcedure;
            _adapter.InsertCommand = DbProviderFactories.GetFactory("fixture").CreateCommand();
            _adapter.InsertCommand.CommandText = "dbo.usp_InsertFixture";
            _adapter.InsertCommand.CommandType = CommandType.Text;
        }

        [GeneratedCode("System.Data.Design.TypedDataSetGenerator", "16.0.0.0")]
        public virtual int Update(DataTable rows) => Adapter.Update(rows);
    }

    internal sealed class GuardedSaveAdapter : SaveAdapter { }
}

namespace Demo.Data.Proxy
{
    [ServiceContract]
    internal interface ISave
    {
        [OperationContract(Action = "http://tempuri.org/ISave/Save")]
        void Save(DataTable rows);
    }
}
