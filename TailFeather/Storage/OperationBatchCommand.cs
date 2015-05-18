using Newtonsoft.Json.Linq;
using Rachis.Commands;

namespace TailFeather.Storage
{
	public class OperationBatchCommand : Command
	{
		public KeyValueOperation[] Batch { get; set; }
	}

    public class GetCommand : Command
    {
        public string Key { get; set; }
    }

	public class CasCommand : Command
	{
		public string Key { get; set; }
		public JToken Value;
		public JToken PrevValue;
	}
}