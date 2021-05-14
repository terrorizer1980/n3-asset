using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;
using System;
using System.ComponentModel;
using System.Numerics;

namespace pnUSDT
{
    [ManifestExtra("Author", "Neo")]
    [ManifestExtra("Email", "dev@neo.org")]
    [ManifestExtra("Description", "This is a contract for USDT on Neo")]
    [ContractPermission("*")]
    public class pnUSDT : SmartContract
    {
        #region Notifications

        [DisplayName("Transfer")]
        public static event Action<UInt160, UInt160, BigInteger> OnTransfer;

        [DisplayName("Notify")]
        public static event Action<string, object> Notify;

        #endregion

        //initial operator
        [InitialValue("NWu2gb7PzhZb4ci9LvW4gBYAQFMGb1s1o7", ContractParameterType.Hash160)]
        private static readonly UInt160 Owner = default;
        private static readonly byte[] SupplyKey = "sk".ToByteArray();
        private static readonly byte[] BalancePrefix = new byte[] { 0x01, 0x01 };
        private static readonly byte[] ContractPrefix = new byte[] { 0x01, 0x02 };
        private static readonly byte[] OwnerKey = "owner".ToByteArray();

        public static readonly StorageMap BalanceMap = new StorageMap(Storage.CurrentContext, BalancePrefix);
        public static readonly StorageMap ContractMap = new StorageMap(Storage.CurrentContext, ContractPrefix);

        private static bool IsOwner() => Runtime.CheckWitness(GetOwner());

        // When this contract address is included in the transaction signature,
        // this method will be triggered as a VerificationTrigger to verify that the signature is correct.
        // For example, this method needs to be called when withdrawing token from the contract.
        public static bool Verify() => IsOwner();

        public static string Symbol() => "pnUSDT";

        public static byte Decimals() => 6;

        public static BigInteger TotalSupply()
        {
            return (BigInteger)ContractMap.Get(SupplyKey);
        }

        private static void SupplyPut(BigInteger value) => ContractMap.Put(SupplyKey, value);

        private static void SupplyIncrease(BigInteger value) => SupplyPut(TotalSupply() + value);

        private static void AssetPut(UInt160 key, BigInteger value) => BalanceMap.Put(key, value);

        private static void AssetIncrease(UInt160 key, BigInteger value) => AssetPut(key, BalanceOf(key) + value);

        private static void AssetReduce(UInt160 key, BigInteger value)
        {
            var oldValue = BalanceOf(key);
            if (oldValue == value)
                Remove(key);
            else
                AssetPut(key, oldValue - value);
        }
        private static void Remove(UInt160 key) => BalanceMap.Delete(key);

        public static void _deploy(object data, bool update)
        {
            if (update) return;
            ContractMap.Put(OwnerKey, Owner);
        }

        public static BigInteger BalanceOf(UInt160 owner)
        {
            return (BigInteger)BalanceMap.Get(owner);
        }

        public static void Init(UInt160 proxyHash, BigInteger supply)
        {
            Assert(IsOwner(), "No authorization.");
            Assert((BigInteger)ContractMap.Get(SupplyKey) == 0, "InitSupply can only be set up one time");

            SupplyPut(supply);
            AssetPut(proxyHash, supply);

            OnTransfer(null, proxyHash, supply);
        }

        public static void Mint(UInt160 proxyHash, BigInteger increase)
        {
            Assert((BigInteger)ContractMap.Get(SupplyKey) > 0, "Need init first");
            Assert(IsOwner(), "No authorization.");
            SupplyIncrease(increase);
            AssetIncrease(proxyHash, increase);

            OnTransfer(null, proxyHash, increase);
        }

        public static bool Transfer(UInt160 from, UInt160 to, BigInteger amount, object data = null)
        {
            if (amount <= 0) throw new Exception("The parameter amount MUST be greater than 0.");
            if (!Runtime.CheckWitness(from) && !from.Equals(Runtime.CallingScriptHash)) throw new Exception("No authorization.");
            if (BalanceOf(from) < amount) throw new Exception("Insufficient balance.");
            if (from == to) return true;

            AssetReduce(from, amount);
            AssetIncrease(to, amount);

            OnTransfer(from, to, amount);

            // Validate payable
            if (ContractManagement.GetContract(to) != null)
                Contract.Call(to, "onNEP17Payment", CallFlags.All, new object[] { from, amount, data });
            return true;
        }

        public static bool TransferOwnership(UInt160 newOwner)
        {
            // transfer contract ownership from current owner to a new owner
            Assert(Runtime.CheckWitness(GetOwner()), "transferOwnerShip: Only allowed to be called by owner.");
            ContractMap.Put(OwnerKey, newOwner);
            return true;
        }

        public static UInt160 GetOwner()
        {
            return ContractMap.Get<UInt160>(OwnerKey);
        }

        public static void Update(ByteString nefFile, string manifest, object data = null)
        {
            if (!IsOwner()) throw new Exception("No authorization.");
            ContractManagement.Update(nefFile, manifest, null);
        }

        public static void Destroy()
        {
            if (!IsOwner()) throw new Exception("No authorization.");
            ContractManagement.Destroy();
        }

        private static void Assert(bool condition, string msg, object result = null, string errorType = "Error")
        {
            if (!condition)
            {
                // TODO: uncomment next line on mainnet
                Notify(errorType, result);
                throw new InvalidOperationException(msg);
            }
        }
    }
}
