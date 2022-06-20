using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blockcore.Indexer.Cirrus.Models;
using Blockcore.Indexer.Cirrus.Storage.Mongo.SmartContracts;
using Blockcore.Indexer.Cirrus.Storage.Mongo.Types;
using Blockcore.Indexer.Core.Client;
using Blockcore.Indexer.Core.Crypto;
using Blockcore.Indexer.Core.Operations.Types;
using Blockcore.Indexer.Core.Settings;
using Blockcore.Indexer.Core.Storage;
using Blockcore.Indexer.Core.Storage.Mongo;
using Blockcore.Indexer.Core.Storage.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace Blockcore.Indexer.Cirrus.Storage.Mongo
{
   public class CirrusMongoData : MongoData, ICirrusStorage
   {
      readonly ICirrusMongoDb mongoDb;

      public CirrusMongoData(
         ILogger<MongoDb> dbLogger,
         SyncConnection connection,
         IOptions<ChainSettings> chainConfiguration,
         GlobalState globalState,
         IMapMongoBlockToStorageBlock mongoBlockToStorageBlock,
         ICryptoClientFactory clientFactory,
         IScriptInterpeter scriptInterpeter,
         IMongoDatabase mongoDatabase,
         ICirrusMongoDb db)
         : base(
            dbLogger,
            connection,
            chainConfiguration,
            globalState,
            mongoBlockToStorageBlock,
            clientFactory,
            scriptInterpeter,
            mongoDatabase,
            db)
      {
         mongoDb = db;
      }

      protected override async Task OnDeleteBlockAsync(SyncBlockInfo block)
      {
         // delete the contracts
         FilterDefinition<CirrusContractTable> contractFilter = Builders<CirrusContractTable>.Filter.Eq(info => info.BlockIndex, block.BlockIndex);
         Task<DeleteResult> contracts = mongoDb.CirrusContractTable.DeleteManyAsync(contractFilter);

         FilterDefinition<CirrusContractCodeTable> contractCodeFilter = Builders<CirrusContractCodeTable>.Filter.Eq(info => info.BlockIndex, block.BlockIndex);
         Task<DeleteResult> contractsCode = mongoDb.CirrusContractCodeTable.DeleteManyAsync(contractCodeFilter);

         await Task.WhenAll(contracts, contractsCode);
      }

      public QueryResult<QueryContractGroup> GroupedContracts()
      {
         var groupedContracts = mongoDb.CirrusContractCodeTable.Aggregate()
            .Group(_ => _.CodeType, ac => new QueryContractGroup
            {
               ContractCodeType = ac.Key,
               Count = ac.Count(),
               ContractHash = ac.First().ContractHash
            })
            .ToList();

         return new QueryResult<QueryContractGroup>
         {
            Items = groupedContracts,
            Offset = 0,
            Limit = groupedContracts.Count,
            Total = groupedContracts.Count
         };
      }

      public QueryResult<QueryContractList> ListContracts(string contractType, int? offset, int limit)
      {
         IMongoQueryable<CirrusContractTable> totalQuary = mongoDb.CirrusContractTable.AsQueryable()
            .Where(q => q.ContractOpcode == "create" && q.ContractCodeType == contractType && q.Success == true);

         int total = totalQuary.Count();

         int itemsToSkip = offset ?? (total < limit ? 0 : total - limit);

         IMongoQueryable<CirrusContractTable> cirrusContract = mongoDb.CirrusContractTable.AsQueryable()
            .Where(q => q.ContractOpcode == "create" &&  q.ContractCodeType == contractType && q.Success == true)
            .OrderBy(b => b.BlockIndex)
            .Skip(itemsToSkip)
            .Take(limit);

         var res = cirrusContract.ToList();

         IEnumerable<QueryContractList> transactions = res.Select(item => new QueryContractList
         {
            ContractAddress = item.NewContractAddress,
            ContractCodeType = item.ContractCodeType,
            Error = item.Error,
            BlockIndex = item.BlockIndex,
            TransactionId = item.TransactionId
         });

         return new QueryResult<QueryContractList>
         {
            Items = transactions,
            Offset = itemsToSkip,
            Limit = limit,
            Total = total
         };
      }

      public Task<NonFungibleToken> GetNonFungibleTokenByIdAsync(string contractAddress, string tokenId)
      {
         return mongoDb.NonFungibleTokenTable.Find(_ =>
               _.Id.ContractAddress == contractAddress && _.Id.TokenId == tokenId)
            .FirstOrDefaultAsync();
      }

      public async Task<QueryStandardToken> GetStandardTokenByIdAsync(string contractAddress, string tokenId)
      {
         var token = await mongoDb.StandardTokenComputedTable.Find(_ => _.ContractAddress == contractAddress)
            .FirstOrDefaultAsync();
         var tokenAmounts = await mongoDb.StandardTokenHolderTable.Find(_ => _.Id.ContractAddress == contractAddress &&
                                                           _.Id.TokenId == tokenId)
            .FirstOrDefaultAsync();

         return new QueryStandardToken
         {
            Name = token.Name,
            Symbol = token.Symbol,
            TotalSupply = token.TotalSupply,
            Address = tokenAmounts.Id.TokenId,
            Amount = tokenAmounts.AmountChangesHistory.Sum(_ => _.Amount)
         };
      }

      public QueryContractCreate ContractCreate(string address)
      {
         IMongoQueryable<CirrusContractTable> cirrusContract = mongoDb.CirrusContractTable.AsQueryable()
            .Where(q => q.NewContractAddress == address);

         var res = cirrusContract.ToList();

         if (res.Count > 1)
            throw new ApplicationException("This is unexpected"); // todo: remove this temporary code

         CirrusContractTable lastEntry = mongoDb.CirrusContractTable
            .AsQueryable()
            .OrderByDescending(b => b.BlockIndex)
            .FirstOrDefault(q => q.ToAddress == address);


         return res.Select(item => new QueryContractCreate
         {
            Success = item.Success,
            ContractAddress = item.NewContractAddress,
            ContractCodeType = item.ContractCodeType,
            GasUsed = item.GasUsed,
            GasPrice = item.GasPrice,
            Amount = item.Amount,
            ContractBalance = lastEntry?.ContractBalance ?? 0,
            FromAddress = item.FromAddress,
            Error = item.Error,
            ContractOpcode = item.ContractOpcode,
            BlockIndex = item.BlockIndex,
            TransactionId = item.TransactionId
         }).FirstOrDefault();
      }

      public QueryResult<QueryContractCall> ContractCall(string address, string filterAddress, int? offset, int limit)
      {
         IMongoQueryable<CirrusContractTable> totalQuary = mongoDb.CirrusContractTable.AsQueryable()
             .Where(q => q.ToAddress == address);

         if (filterAddress != null)
         {
            totalQuary = totalQuary.Where(q => q.FromAddress == filterAddress);
         }

         int total = totalQuary.Count();

         IMongoQueryable<CirrusContractTable> cirrusContract = mongoDb.CirrusContractTable.AsQueryable()
            .Where(q => q.ToAddress == address);

         if (filterAddress != null)
         {
            cirrusContract = cirrusContract.Where(q => q.FromAddress == filterAddress);
         }

         int itemsToSkip = offset ?? (total < limit ? 0 : total - limit);

         cirrusContract = cirrusContract
            .OrderBy(b => b.BlockIndex)
            .Skip(itemsToSkip)
            .Take(limit);

         var res = cirrusContract.ToList();

         IEnumerable<QueryContractCall> transactions = res.Select(item => new QueryContractCall
         {
            Success = item.Success,
            MethodName = item.MethodName,
            ToAddress = item.NewContractAddress,
            GasUsed = item.GasUsed,
            GasPrice = item.GasPrice,
            Amount = item.Amount,
            ContractBalance = item.ContractBalance,
            FromAddress = item.FromAddress,
            Error = item.Error,
            BlockIndex = item.BlockIndex,
            TransactionId = item.TransactionId
         });

         return new QueryResult<QueryContractCall>
         {
            Items = transactions,
            Offset = itemsToSkip,
            Limit = limit,
            Total = total
         };
      }

      public QueryContractTransaction ContractTransaction(string transacitonId)
      {
         IMongoQueryable<CirrusContractTable> cirrusContract = mongoDb.CirrusContractTable.AsQueryable()
            .Where(q => q.TransactionId == transacitonId);

         var res = cirrusContract.ToList();

         if (res.Count > 1)
            throw new ApplicationException("This is unexpected"); // todo: remove this temporary code

         return res.Select(item => new QueryContractTransaction
         {
            Success = item.Success,
            NewContractAddress = item.NewContractAddress,
            ContractCodeType = item.ContractCodeType,
            GasUsed = item.GasUsed,
            GasPrice = item.GasPrice,
            Amount = item.Amount,
            ContractBalance = item.ContractBalance,
            FromAddress = item.FromAddress,
            ToAddress = item.ToAddress,
            Logs = item.Logs,
            MethodName = item.MethodName,
            PostState = item.PostState,
            Error = item.Error,
            ContractOpcode = item.ContractOpcode,
            BlockIndex = item.BlockIndex,
            TransactionId = item.TransactionId
         }).FirstOrDefault();
      }

      public QueryContractCode ContractCode(string address)
      {
         IMongoQueryable<CirrusContractCodeTable> cirrusContractCode = mongoDb.CirrusContractCodeTable.AsQueryable()
            .Where(q => q.ContractAddress == address);

         var res = cirrusContractCode.ToList();

         return res.Select(item => new QueryContractCode
         {
            CodeType = item.CodeType,
            ByteCode = item.ByteCode,
            ContractHash = item.ContractHash,
            SourceCode = item.SourceCode
         }).FirstOrDefault();
      }

      public async Task<QueryResult<QueryAddressAsset>> GetAssetsForAddressAsync(string address, int? offset, int limit)
      {
         int total = await mongoDb.NonFungibleTokenTable
            .AsQueryable()
            .CountAsync(_ => _.Owner == address);

         int startPosition = offset ?? total - limit;
         int endPosition = startPosition + limit;

         var dbTokens = await mongoDb.NonFungibleTokenTable.Aggregate()
            .Match(_ => _.Owner == address)
            //.SortBy(_ => _.) TODO David check if we need sorting for the FE
            .Skip(startPosition)
            .Limit(endPosition)
            .ToListAsync();

         var tokens = dbTokens.Select(_ => new QueryAddressAsset
         {
            Creator = _.Creator,
            ContractId = _.Id.ContractAddress,
            Id = _.Id.TokenId,
            Uri = _.Uri,
            IsBurned = _.IsBurned,
            TransactionId = _.SalesHistory.LastOrDefault()?.TransactionId,
            PricePaid = GetPricePaidFromHistory(_.SalesHistory)
         });
         return new QueryResult<QueryAddressAsset>
         {
            Items = tokens, Limit = limit, Offset = offset ?? 0, Total = total
         };
      }

      private static long GetPricePaidFromHistory(IEnumerable<TokenSaleEvent> saleEvents)
      {
         if (!saleEvents.Any())
            return 0;

         var last = saleEvents.Last();

         return last switch
         {
            Auction auction => auction.HighestBid,
            OnSale sale => sale.Price,
            _ => 0
         };
      }
   }
}
