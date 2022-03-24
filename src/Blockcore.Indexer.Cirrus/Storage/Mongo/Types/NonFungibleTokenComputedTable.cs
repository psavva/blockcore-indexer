using System.Collections.Generic;

namespace Blockcore.Indexer.Cirrus.Storage.Mongo.Types;

public class NonFungibleTokenComputedTable : SmartContractComputedBase
{
   public string Name { get; set; }
   public string Symbol { get; set; }

   public string Owner { get; set; }
   public override string ContractType { get; } = "NonFungibleToken";

   public List<Token> Tokens { get; set; }
   public bool OwnerOnlyMinting { get; set; }
   public string PendingOwner { get; set; }

   public List<string> PreviousOwners { get; set; }
}

public class Token
{
   public long Id { get; set; }
   public string Address { get; set; }
   public string Uri { get; set; }

   public string OwnerAddress { get; set; }
   public bool IsBurnet { get; set; }
}
