﻿using Com.DanLiris.Service.Purchasing.Lib.Helpers;
using Com.DanLiris.Service.Purchasing.Lib.Interfaces;
using Com.DanLiris.Service.Purchasing.Lib.Models.DeliveryOrderModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.ExternalPurchaseOrderModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.InternalPurchaseOrderModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.PurchaseRequestModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.UnitPaymentOrderModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.UnitReceiptNoteModel;
using Com.Moonlay.Models;
using Com.Moonlay.NetCore.Lib;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Com.DanLiris.Service.Purchasing.Lib.Facades
{
    public class UnitPaymentOrderFacade : IUnitPaymentOrderFacade
    {
        private readonly PurchasingDbContext dbContext;
        private readonly DbSet<UnitPaymentOrder> dbSet;

        private string USER_AGENT = "Facade";

        public UnitPaymentOrderFacade(PurchasingDbContext dbContext)
        {
            this.dbContext = dbContext;
            this.dbSet = dbContext.Set<UnitPaymentOrder>();
        }

        public Tuple<List<UnitPaymentOrder>, int, Dictionary<string, string>> Read(int Page = 1, int Size = 25, string Order = "{}", string Keyword = null, string Filter = "{}")
        {
            IQueryable<UnitPaymentOrder> Query = this.dbSet;

            List<string> searchAttributes = new List<string>()
            {
                "UPONo", "DivisionName", "SupplierName", "Items.URNNo", "Items.DONo"
            };

            Query = QueryHelper<UnitPaymentOrder>.ConfigureSearch(Query, searchAttributes, Keyword);

            Query = Query.Select(s => new UnitPaymentOrder
            {
                Id = s.Id,
                DivisionId = s.DivisionId,
                DivisionCode = s.DivisionCode,
                DivisionName = s.DivisionName,
                SupplierId = s.SupplierId,
                SupplierCode = s.SupplierCode,
                SupplierName = s.SupplierName,
                CategoryCode=s.CategoryCode,
                CategoryId=s.CategoryId,
                CategoryName=s.CategoryName,
                Date = s.Date,
                UPONo = s.UPONo,
                DueDate=s.DueDate,
                UseIncomeTax=s.UseIncomeTax,
                UseVat=s.UseVat,
                CurrencyCode=s.CurrencyCode,
                CurrencyDescription=s.CurrencyDescription,
                CurrencyId=s.CurrencyId,
                CurrencyRate=s.CurrencyRate,
                Items = s.Items.Select(i => new UnitPaymentOrderItem
                {
                    URNNo = i.URNNo,
                    DONo = i.DONo,
                    Details=i.Details.ToList()
                }).ToList(),
                CreatedBy = s.CreatedBy,
                LastModifiedUtc = s.LastModifiedUtc,
            });

            Dictionary<string, string> FilterDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Filter);
            Query = QueryHelper<UnitPaymentOrder>.ConfigureFilter(Query, FilterDictionary);

            Dictionary<string, string> OrderDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Order);
            Query = QueryHelper<UnitPaymentOrder>.ConfigureOrder(Query, OrderDictionary);

            Pageable<UnitPaymentOrder> pageable = new Pageable<UnitPaymentOrder>(Query, Page - 1, Size);
            List<UnitPaymentOrder> Data = pageable.Data.ToList();
            int TotalData = pageable.TotalCount;

            return Tuple.Create(Data, TotalData, OrderDictionary);
        }

        public UnitPaymentOrder ReadById(int id)
        {
            var Result = dbSet.Where(m => m.Id == id)
                .Include(m => m.Items)
                    .ThenInclude(i => i.Details)
                .FirstOrDefault();

            return Result;
        }

        public async Task<int> Create(UnitPaymentOrder model, string user, bool isImport, int clientTimeZoneOffset = 7)
        {
            int Created = 0;

            using(var transaction = dbContext.Database.BeginTransaction())
            {
                try
                {
                    EntityExtension.FlagForCreate(model, user, USER_AGENT);
                    model.UPONo = await GenerateNo(model, isImport, clientTimeZoneOffset);
                    foreach (var item in model.Items)
                    {
                        EntityExtension.FlagForCreate(item, user, USER_AGENT);
                        foreach (var detail in item.Details)
                        {
                            SetPOItemIdEPONo(detail);
                            EntityExtension.FlagForCreate(detail, user, USER_AGENT);
                        }
                        SetPaid(item, true, user);
                    }

                    SetDueDate(model);

                    this.dbSet.Add(model);

                    Created = await dbContext.SaveChangesAsync();

                    foreach (var item in model.Items)
                    {
                        foreach (var detail in item.Details)
                        {
                            SetStatus(detail, user);
                        }
                    }

                    await dbContext.SaveChangesAsync();
                    transaction.Commit();
                }
                catch (Exception e)
                {
                    transaction.Rollback();
                    throw new Exception(e.Message);
                }
            }

            return Created;
        }

        public async Task<int> Update(int id, UnitPaymentOrder model, string user)
        {
            int Updated = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    var existingModel = this.dbSet.AsNoTracking()
                        .Include(d => d.Items)
                            .ThenInclude(d => d.Details)
                        .SingleOrDefault(m => m.Id == id && !m.IsDeleted);

                    if (existingModel != null && id == model.Id)
                    {
                        EntityExtension.FlagForUpdate(model, user, USER_AGENT);

                        foreach (var item in model.Items)
                        {
                            if (item.Id == 0)
                            {
                                EntityExtension.FlagForCreate(item, user, USER_AGENT);
                                foreach (var detail in item.Details)
                                {
                                    SetPOItemIdEPONo(detail);
                                    EntityExtension.FlagForCreate(detail, user, USER_AGENT);
                                }
                            }
                            else
                            {
                                EntityExtension.FlagForUpdate(item, user, USER_AGENT);
                                foreach (var detail in item.Details)
                                {
                                    EntityExtension.FlagForUpdate(detail, user, USER_AGENT);
                                }
                            }

                            SetPaid(item, true, user);
                        }

                        SetDueDate(model);

                        this.dbContext.Update(model);

                        foreach (var existingItem in existingModel.Items)
                        {
                            var newItem = model.Items.FirstOrDefault(i => i.Id == existingItem.Id);
                            if (newItem == null)
                            {
                                EntityExtension.FlagForDelete(existingItem, user, USER_AGENT);
                                this.dbContext.UnitPaymentOrderItems.Update(existingItem);
                                foreach (var existingDetail in existingItem.Details)
                                {
                                    EntityExtension.FlagForDelete(existingDetail, user, USER_AGENT);
                                    this.dbContext.UnitPaymentOrderDetails.Update(existingDetail);
                                }

                                SetPaid(existingItem, false, user);
                            }
                        }

                        Updated = await dbContext.SaveChangesAsync();

                        foreach (var item in model.Items)
                        {
                            foreach (var detail in item.Details)
                            {
                                SetStatus(detail, user);
                            }
                        }

                        await dbContext.SaveChangesAsync();
                        transaction.Commit();
                    }
                    else
                    {
                        throw new Exception("Invalid Id");
                    }
                }
                catch (Exception e)
                {
                    transaction.Rollback();
                    throw new Exception(e.Message);
                }
            }

            return Updated;
        }

        public async Task<int> Delete(int id, string user)
        {
            int Deleted = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    var model = this.dbSet
                        .Include(d => d.Items)
                            .ThenInclude(d => d.Details)
                        .SingleOrDefault(m => m.Id == id && !m.IsDeleted);

                    EntityExtension.FlagForDelete(model, user, USER_AGENT);

                    foreach (var item in model.Items)
                    {
                        EntityExtension.FlagForDelete(item, user, USER_AGENT);
                        foreach (var detail in item.Details)
                        {
                            EntityExtension.FlagForDelete(detail, user, USER_AGENT);
                        }

                        SetPaid(item, false, user);
                    }

                    Deleted = await dbContext.SaveChangesAsync();

                    foreach (var item in model.Items)
                    {
                        foreach (var detail in item.Details)
                        {
                            SetStatus(detail, user);
                        }
                    }

                    await dbContext.SaveChangesAsync();
                    transaction.Commit();
                }
                catch (Exception e)
                {
                    transaction.Rollback();
                    throw new Exception(e.Message);
                }
            }

            return Deleted;
        }

        async Task<string> GenerateNo(UnitPaymentOrder model, bool isImport, int clientTimeZoneOffset)
        {
            string Year = model.Date.ToOffset(new TimeSpan(clientTimeZoneOffset, 0, 0)).ToString("yy");
            string Month = model.Date.ToOffset(new TimeSpan(clientTimeZoneOffset, 0, 0)).ToString("MM");
            string Supplier = isImport ? "NKI" : "NKL";
            string TG = "";
            if (model.DivisionName.ToUpper().Equals("GARMENT"))
            {
                TG = "G-";
            }
            else if(model.DivisionName.ToUpper().Equals("UMUM") || 
                model.DivisionName.ToUpper().Equals("SPINNING") ||
                model.DivisionName.ToUpper().Equals("FINISHING & PRINTING") ||
                model.DivisionName.ToUpper().Equals("UTILITY") ||
                model.DivisionName.ToUpper().Equals("WEAVING"))
            {
                TG = "T-";
            }

            string no = $"{Year}-{Month}-{TG}{Supplier}-";
            int Padding = isImport ? 3 : 4;

            var lastNo = await dbSet.Where(w => w.UPONo.StartsWith(no) && !w.UPONo.EndsWith("L") && !w.IsDeleted).OrderByDescending(o => o.UPONo).FirstOrDefaultAsync();

            if (lastNo == null)
            {
                return no + "1".PadLeft(Padding, '0');
            }
            else
            {
                int lastNoNumber = int.Parse(lastNo.UPONo.Replace(no, "")) + 1;
                return no + lastNoNumber.ToString().PadLeft(Padding, '0');
            }
        }

        private void SetPOItemIdEPONo(UnitPaymentOrderDetail detail)
        {
            ExternalPurchaseOrderDetail EPODetail = dbContext.ExternalPurchaseOrderDetails.Single(m => m.Id == detail.EPODetailId);
            detail.POItemId = EPODetail.POItemId;

            detail.EPONo = dbContext.ExternalPurchaseOrders.Single(m => m.Items.Any(i => i.Id == EPODetail.EPOItemId)).EPONo;
        }

        private void SetPaid(UnitPaymentOrderItem item, bool isPaid, string username)
        {
            UnitReceiptNote unitReceiptNote = dbContext.UnitReceiptNotes.Include(a=>a.Items).Single(m => m.Id == item.URNId);
            foreach(var itemURN in unitReceiptNote.Items)
            {
                itemURN.IsPaid = isPaid;
            }
            bool flagIsPaid = true;
            foreach (var itemURNPaid in unitReceiptNote.Items)
            {
                if (itemURNPaid.IsPaid==false)
                {
                    flagIsPaid = false;
                }
            }
            unitReceiptNote.IsPaid = flagIsPaid;
            EntityExtension.FlagForUpdate(unitReceiptNote, username, USER_AGENT);
        }

        private void SetStatus(UnitPaymentOrderDetail detail, string username)
        {
            ExternalPurchaseOrderDetail EPODetail = dbContext.ExternalPurchaseOrderDetails.Single(m => m.Id == detail.EPODetailId);
            InternalPurchaseOrderItem POItem = dbContext.InternalPurchaseOrderItems.Single(m => m.Id == EPODetail.POItemId);

            List<long> EPODetailIds = dbContext.ExternalPurchaseOrderDetails.Where(m => m.POItemId == POItem.Id).Select(m => m.Id).ToList();
            List<long> URNItemIds = dbContext.UnitReceiptNoteItems.Where(m => EPODetailIds.Contains(m.EPODetailId)).Select(m => m.Id).ToList();

            var totalReceiptQuantity = dbContext.UnitPaymentOrderDetails.AsNoTracking().Where(m => m.IsDeleted == false && URNItemIds.Contains(m.URNItemId)).Sum(m => m.ReceiptQuantity);
            if (totalReceiptQuantity > 0)
            {
                if (totalReceiptQuantity < EPODetail.DealQuantity)
                {
                    POItem.Status = "Sudah dibuat SPB sebagian";
                }
                else
                {
                    POItem.Status = "Sudah dibuat SPB semua";
                }
            }
            else if (totalReceiptQuantity == 0)
            {
                if (EPODetail.DOQuantity >= EPODetail.DealQuantity)
                {
                    if (EPODetail.ReceiptQuantity < EPODetail.DealQuantity)
                    {
                        POItem.Status = "Barang sudah diterima Unit parsial";
                    }
                    else
                    {
                        POItem.Status = "Barang sudah diterima Unit semua";
                    }
                }
                else
                {
                    POItem.Status = "Barang sudah diterima Unit parsial";
                }
            }
            EntityExtension.FlagForUpdate(POItem, username, USER_AGENT);
        }

        private void SetDueDate(UnitPaymentOrder model)
        {
            List<DateTimeOffset> DueDates = new List<DateTimeOffset>();
            foreach (var item in model.Items)
            {
                var unitReceiptNoteDate = dbContext.UnitReceiptNotes.Single(m => m.Id == item.URNId).ReceiptDate;
                foreach (var detail in item.Details)
                {
                    var PaymentDueDays = dbContext.ExternalPurchaseOrders.Single(m => m.EPONo.Equals(detail.EPONo)).PaymentDueDays;
                    DueDates.Add(unitReceiptNoteDate.AddDays(Double.Parse(PaymentDueDays ?? "0")));
                }
            }
            model.DueDate = DueDates.Min();
        }

        public Tuple<List<UnitPaymentOrder>, int, Dictionary<string, string>> ReadSpb(int Page = 1, int Size = 25, string Order = "{}", string Keyword = null, string Filter = "{}")
        {
            IQueryable<UnitPaymentOrder> Query = this.dbSet;

            List<string> searchAttributes = new List<string>()
            {
                "UPONo", "DivisionName", "SupplierName", "Items.URNNo", "Items.DONo"
            };

            Query = QueryHelper<UnitPaymentOrder>.ConfigureSearch(Query, searchAttributes, Keyword);

            Query = Query.Select(s => new UnitPaymentOrder
            {
                Id = s.Id,
                DivisionId = s.DivisionId,
                DivisionCode = s.DivisionCode,
                DivisionName = s.DivisionName,
                SupplierId = s.SupplierId,
                SupplierCode = s.SupplierCode,
                SupplierName = s.SupplierName,
                CategoryId = s.CategoryId,
                CategoryCode = s.CategoryCode,
                CategoryName = s.CategoryName,
                CurrencyId = s.CurrencyId,
                CurrencyCode = s.CurrencyCode,
                CurrencyRate = s.CurrencyRate,
                CurrencyDescription = s.CurrencyDescription,
                PaymentMethod = s.PaymentMethod,
                InvoiceNo = s.InvoiceNo,
                InvoiceDate = s.InvoiceDate,
                PibNo = s.PibNo,
                UseIncomeTax = s.UseIncomeTax,
                IncomeTaxId = s.IncomeTaxId,
                IncomeTaxName = s.IncomeTaxName,
                IncomeTaxRate = s.IncomeTaxRate,
                IncomeTaxNo = s.IncomeTaxNo,
                IncomeTaxDate = s.IncomeTaxDate,
                UseVat = s.UseVat,
                VatNo = s.VatNo,
                VatDate = s.VatDate,
                Remark = s.Remark,
                DueDate = s.DueDate,
                Date = s.Date,
                UPONo = s.UPONo,
                Items = s.Items.Select(i => new UnitPaymentOrderItem
                {
                    UPOId = i.UPOId,
                    URNId = i.URNId,
                    URNNo = i.URNNo,
                    DOId = i.DOId,
                    DONo = i.DONo,
                    Details = i.Details.Select(j => new UnitPaymentOrderDetail
                    {
                        Id = j.Id,
                        UPOItemId = j.UPOItemId,
                        URNItemId = j.URNItemId,
                        EPONo = j.EPONo,
                        PRId = j.PRId,
                        PRNo = j.PRNo,
                        PRItemId = j.PRItemId,
                        ProductId = j.ProductId,
                        ProductCode = j.ProductCode,
                        ProductName = j.ProductName,
                        ProductRemark = j.ProductRemark,
                        ReceiptQuantity = j.ReceiptQuantity,
                        UomId = j.UomId,
                        UomUnit = j.UomUnit,
                        PricePerDealUnit = j.PricePerDealUnit,
                        PriceTotal = j.PriceTotal,
                        PricePerDealUnitCorrection = j.PricePerDealUnitCorrection,
                        PriceTotalCorrection = j.PriceTotalCorrection,
                        QuantityCorrection = j.QuantityCorrection,
                        //Duedate = s.DueDate,
                    }).ToList()
                }).ToList(),
                CreatedBy = s.CreatedBy,
                LastModifiedUtc = s.LastModifiedUtc,
            });

            Dictionary<string, string> FilterDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Filter);
            Query = QueryHelper<UnitPaymentOrder>.ConfigureFilter(Query, FilterDictionary);

            Dictionary<string, string> OrderDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Order);
            Query = QueryHelper<UnitPaymentOrder>.ConfigureOrder(Query, OrderDictionary);

            Pageable<UnitPaymentOrder> pageable = new Pageable<UnitPaymentOrder>(Query, Page - 1, Size);
            List<UnitPaymentOrder> Data = pageable.Data.ToList();
            int TotalData = pageable.TotalCount;

            return Tuple.Create(Data, TotalData, OrderDictionary);
        }

        public Tuple<List<UnitPaymentOrder>, int, Dictionary<string, string>> ReadPositionFiltered(int Page = 1, int Size = 25, string Order = "{}", string Keyword = null, string Filter = "{}")
        {
            IQueryable<UnitPaymentOrder> Query = this.dbSet;

            List<string> searchAttributes = new List<string>()
            {
                "UPONo", "DivisionName", "SupplierName", "Items.URNNo", "Items.DONo"
            };

            Query = QueryHelper<UnitPaymentOrder>.ConfigureSearch(Query, searchAttributes, Keyword);

            Query = Query.Select(s => new UnitPaymentOrder
            {
                Id = s.Id,
                DivisionId = s.DivisionId,
                DivisionCode = s.DivisionCode,
                DivisionName = s.DivisionName,
                SupplierId = s.SupplierId,
                SupplierCode = s.SupplierCode,
                SupplierName = s.SupplierName,
                CategoryId = s.CategoryId,
                CategoryCode = s.CategoryCode,
                CategoryName = s.CategoryName,
                CurrencyId = s.CurrencyId,
                CurrencyCode = s.CurrencyCode,
                CurrencyRate = s.CurrencyRate,
                CurrencyDescription = s.CurrencyDescription,
                PaymentMethod = s.PaymentMethod,
                InvoiceNo = s.InvoiceNo,
                InvoiceDate = s.InvoiceDate,
                PibNo = s.PibNo,
                UseIncomeTax = s.UseIncomeTax,
                IncomeTaxId = s.IncomeTaxId,
                IncomeTaxName = s.IncomeTaxName,
                IncomeTaxRate = s.IncomeTaxRate,
                IncomeTaxNo = s.IncomeTaxNo,
                IncomeTaxDate = s.IncomeTaxDate,
                UseVat = s.UseVat,
                VatNo = s.VatNo,
                VatDate = s.VatDate,
                Remark = s.Remark,
                DueDate = s.DueDate,
                Date = s.Date,
                UPONo = s.UPONo,
                Position = s.Position,
                Items = s.Items.Select(i => new UnitPaymentOrderItem
                {
                    UPOId = i.UPOId,
                    URNId = i.URNId,
                    URNNo = i.URNNo,
                    DOId = i.DOId,
                    DONo = i.DONo,
                    Details = i.Details.Select(j => new UnitPaymentOrderDetail
                    {
                        Id = j.Id,
                        UPOItemId = j.UPOItemId,
                        URNItemId = j.URNItemId,
                        EPONo = j.EPONo,
                        PRId = j.PRId,
                        PRNo = j.PRNo,
                        PRItemId = j.PRItemId,
                        ProductId = j.ProductId,
                        ProductCode = j.ProductCode,
                        ProductName = j.ProductName,
                        ProductRemark = j.ProductRemark,
                        ReceiptQuantity = j.ReceiptQuantity,
                        UomId = j.UomId,
                        UomUnit = j.UomUnit,
                        PricePerDealUnit = j.PricePerDealUnit,
                        PriceTotal = j.PriceTotal,
                        PricePerDealUnitCorrection = j.PricePerDealUnitCorrection,
                        PriceTotalCorrection = j.PriceTotalCorrection,
                        QuantityCorrection = j.QuantityCorrection,
                        //Duedate = s.DueDate,
                    }).ToList()
                }).ToList(),
                CreatedBy = s.CreatedBy,
                LastModifiedUtc = s.LastModifiedUtc,
            });

            Dictionary<string, List<int>> FilterDictionary = JsonConvert.DeserializeObject<Dictionary<string, List<int>>>(Filter);
            if(FilterDictionary.Keys.FirstOrDefault() == "position")
            {
                List<int> filteredPosition = FilterDictionary.GetValueOrDefault("position");
                Query = Query.Where(x => filteredPosition.Contains(x.Position));
            }

            Dictionary<string, string> OrderDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Order);
            Query = QueryHelper<UnitPaymentOrder>.ConfigureOrder(Query, OrderDictionary);

            Pageable<UnitPaymentOrder> pageable = new Pageable<UnitPaymentOrder>(Query, Page - 1, Size);
            List<UnitPaymentOrder> Data = pageable.Data.ToList();
            int TotalData = pageable.TotalCount;

            return Tuple.Create(Data, TotalData, OrderDictionary);
        }

        #region ForPDF

        public UnitReceiptNote GetUnitReceiptNote(long URNId)
        {
            return dbContext.UnitReceiptNotes.Single(m => m.Id == URNId);
        }

        public ExternalPurchaseOrder GetExternalPurchaseOrder(string EPONo)
        {
            return dbContext.ExternalPurchaseOrders.Single(m => m.EPONo.Equals(EPONo));
        }

        #endregion

        #region MonitoringAll
        public IQueryable<ViewModels.UnitPaymentOrderViewModel.UnitPaymentOrderReportViewModel> GetReportQueryAll(string unitId, string supplierId, DateTime? dateFrom, DateTime? dateTo, int offset)
        {
            DateTime DateFrom = dateFrom == null ? new DateTime(1970, 1, 1) : (DateTime)dateFrom;
            DateTime DateTo = dateTo == null ? DateTime.Now : (DateTime)dateTo;

            var Query = (from a in dbContext.UnitPaymentOrders
                         join b in dbContext.UnitPaymentOrderItems on a.Id equals b.UPOId
                         join c in dbContext.UnitPaymentOrderDetails on b.Id equals c.UPOItemId
                         join d in dbContext.PurchaseRequests on c.PRId equals d.Id
                         join e in dbContext.UnitReceiptNotes on b.URNNo equals e.URNNo
                         join f in dbContext.ExternalPurchaseOrders on c.EPONo equals f.EPONo

                         //Conditions
                         where a.IsDeleted == false
                                && b.IsDeleted == false
                                && c.IsDeleted == false
                                && d.IsDeleted == false
                                && e.IsDeleted == false
                                && f.IsDeleted == false
                                && a.SupplierId == (string.IsNullOrWhiteSpace(supplierId) ? a.SupplierId : supplierId)
                                && e.UnitId == (string.IsNullOrWhiteSpace(unitId) ? e.UnitId : unitId)
                                && a.Date.AddHours(offset).Date >= DateFrom.Date
                                && a.Date.AddHours(offset).Date <= DateTo.Date

                         select new ViewModels.UnitPaymentOrderViewModel.UnitPaymentOrderReportViewModel
                         {
                             tglspb = a.Date,
                             nospb = a.UPONo,
                             namabrg = c.ProductName,
                             satuan = c.UomUnit,
                             jumlah = c.ReceiptQuantity,
                             hrgsat = c.PricePerDealUnit,
                             jumlahhrg = c.PriceTotal,
                             ppn = a.UseVat == true ? (c.PriceTotal * 10) / 100 : 0,
                             total = c.PriceTotal + (a.UseVat == true ? (c.PriceTotal * 10) / 100 : 0),
                             pph = a.IncomeTaxRate * c.PriceTotal,
                             tglpr = d.Date,
                             nopr = c.PRNo,
                             tglbon = e.ReceiptDate,
                             nobon = b.URNNo,
                             tglinv = a.InvoiceDate,
                             noinv = a.InvoiceNo,
                             kodesupplier = a.SupplierCode,
                             supplier = a.SupplierName,
                             div = a.DivisionName,
                             adm = a.CreatedBy,
                             matauang = a.CurrencyCode,
                             kategori = a.CategoryName,
                             unit = e.UnitName,
                             jt = e.ReceiptDate.AddDays(Convert.ToDouble(f.PaymentDueDays)),
                             qtycorrection = c.QuantityCorrection,
                             pricecorrection = c.PricePerDealUnitCorrection,
                             totalpricecorrection = c.PriceTotalCorrection,



                         });
            return Query;
        }

        public Tuple<List<ViewModels.UnitPaymentOrderViewModel.UnitPaymentOrderReportViewModel>, int> GetReportAll(string unitId, string supplierId, DateTime? dateFrom, DateTime? dateTo, int page, int size, string Order, int offset)
        {
            var Query = GetReportQueryAll(unitId, supplierId, dateFrom, dateTo, offset);

            Dictionary<string, string> OrderDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Order);
            if (OrderDictionary.Count.Equals(0))
            {
                Query = Query.OrderByDescending(b => b.no);
            }

            Pageable<ViewModels.UnitPaymentOrderViewModel.UnitPaymentOrderReportViewModel> pageable = new Pageable<ViewModels.UnitPaymentOrderViewModel.UnitPaymentOrderReportViewModel>(Query, page - 1, size);
            List<ViewModels.UnitPaymentOrderViewModel.UnitPaymentOrderReportViewModel> Data = pageable.Data.ToList<ViewModels.UnitPaymentOrderViewModel.UnitPaymentOrderReportViewModel>();
            int TotalData = pageable.TotalCount;

            return Tuple.Create(Data, TotalData);
        }

        public MemoryStream GenerateExcel(string unitId, string supplierId, DateTime? dateFrom, DateTime? dateTo, int offset)
        {
            var Query = GetReportQueryAll(unitId, supplierId, dateFrom, dateTo, offset);
            Query = Query.OrderByDescending(b => b.tglspb);
            DataTable result = new DataTable();
            //No	Unit	Budget	Kategori	Tanggal PR	Nomor PR	Kode Barang	Nama Barang	Jumlah	Satuan	Tanggal Diminta Datang	Status	Tanggal Diminta Datang Eksternal


            result.Columns.Add(new DataColumn() { ColumnName = "No", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "TglSPB", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "NoSPB", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "NamaBrg", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Sat", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Jml", DataType = typeof(Double) });
            result.Columns.Add(new DataColumn() { ColumnName = "HrgSat", DataType = typeof(Double) });
            result.Columns.Add(new DataColumn() { ColumnName = "JmlHrg", DataType = typeof(Double) });
            result.Columns.Add(new DataColumn() { ColumnName = "Ppn", DataType = typeof(Double) });
            result.Columns.Add(new DataColumn() { ColumnName = "Total", DataType = typeof(Double) });
            result.Columns.Add(new DataColumn() { ColumnName = "Pph", DataType = typeof(Double) });
            result.Columns.Add(new DataColumn() { ColumnName = "TglPR", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "NoPR", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "TglBon", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "NoBon", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "TglInv", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "NoInv", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "JT", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "KdSupp", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Supp", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Unit", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Div", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "ADM", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "MtUang", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Kat", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "QtyKoreksi", DataType = typeof(Double) });
            result.Columns.Add(new DataColumn() { ColumnName = "HargaKoreksi", DataType = typeof(Double) });
            result.Columns.Add(new DataColumn() { ColumnName = "TotalKoreksi", DataType = typeof(Double) });

            if (Query.ToArray().Count() == 0)
                result.Rows.Add("", "", "", "", "", 0, 0, 0, 0, 0, 0, "", "", "", "", "", "", "", "", "", "", "", "", "", "", 0, 0, 0); // to allow column name to be generated properly for empty data as template
            else
            {
                int index = 0;
                foreach (var item in Query)
                {
                    index++;
                    string tglspb = item.tglspb == null ? "-" : item.tglspb.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd-MM-yyyy", new CultureInfo("id-ID"));
                    string tglpr = item.tglpr == null ? "-" : item.tglpr.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd-MM-yyyy", new CultureInfo("id-ID"));
                    string tglbon = item.tglbon == null ? "-" : item.tglbon.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd-MM-yyyy", new CultureInfo("id-ID"));
                    string tglinv = item.tglinv == null ? "-" : item.tglinv.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd-MM-yyyy", new CultureInfo("id-ID"));
                    string jt = item.jt == null ? "-" : item.jt.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd-MM-yyyy", new CultureInfo("id-ID"));
                    result.Rows.Add(index, tglspb, item.nospb, item.namabrg, item.satuan, item.jumlah, item.hrgsat, item.jumlahhrg, item.ppn, item.total, item.pph, tglpr, item.nopr, tglbon
                       , item.nobon, tglinv, item.noinv, jt, item.kodesupplier, item.supplier, item.unit, item.div, item.adm, item.matauang, item.kategori, item.qtycorrection, item.pricecorrection, item.totalpricecorrection);
                }
            }

            return Excel.CreateExcel(new List<KeyValuePair<DataTable, string>>() { new KeyValuePair<DataTable, string>(result, "Territory") }, true);
        }
        #endregion

        public IQueryable<ViewModels.UnitPaymentOrderViewModel.UnitPaymentOrderGenerateDataViewModel> GetReportQuery(DateTime? dateFrom, DateTime? dateTo, int offset)
        {
            DateTime DateFrom = dateFrom == null ? new DateTime(1970, 1, 1) : (DateTime)dateFrom;
            DateTime DateTo = dateTo == null ? DateTime.Now : (DateTime)dateTo;
            var Query = (from a in dbContext.UnitPaymentOrders
                         join b in dbContext.UnitPaymentOrderItems on a.Id equals b.UPOId
                         join c in dbContext.UnitPaymentOrderDetails on b.Id equals c.UPOItemId
                         join d in dbContext.UnitReceiptNotes on b.URNId equals d.Id
                         where a.IsDeleted == false && b.IsDeleted == false && c.IsDeleted == false && d.IsDeleted == false &&
                               a.Date.AddHours(offset).Date >= DateFrom.Date &&
                               a.Date.AddHours(offset).Date <= DateTo.Date
                         select new ViewModels.UnitPaymentOrderViewModel.UnitPaymentOrderGenerateDataViewModel
                         {
                             UPONo = a.UPONo,
                             UPODate = a.Date,
                             SupplierCode = a.SupplierCode,
                             SupplierName = a.SupplierName,
                             CategoryName = a.CategoryName,
                             InvoiceNo = a.InvoiceNo,
                             InvoiceDate = a.InvoiceDate,
                             DueDate = a.DueDate,
                             UPORemark = a.Remark,
                             UseVat = a.UseVat ? "YA " : "TIDAK",
                             VatNo = a.VatNo,
                             VatDate = a.VatDate,
                             UseIncomeTax = a.UseIncomeTax ? "YA " : "TIDAK",
                             IncomeTaxName = a.IncomeTaxName,
                             IncomeTaxRate = a.IncomeTaxRate,
                             IncomeTaxNo = a.IncomeTaxNo,
                             IncomeTaxDate = a.IncomeTaxDate,
                             EPONO = c.EPONo,
                             PRNo = c.PRNo,
                             AccountNo = "",
                             IncludedPPN = "TIDAK",
                             Printed = "-",
                             ProductCode = c.ProductCode,
                             ProductName = c.ProductName,
                             ReceiptQty = c.ReceiptQuantity,
                             UOMUnit = c.UomUnit,
                             PricePerDealUnit = c.PricePerDealUnit,
                             CurrencyCode = a.CurrencyCode,
                             CurrencyRate = a.CurrencyRate,
                             PriceTotal = c.PriceTotal,
                             URNNo = b.URNNo,
                             URNDate = d.ReceiptDate,
                             UserCreated = a.CreatedBy,
                             PaymentMethod = a.PaymentMethod,
                         }
                         );
            return Query;
        }

        public MemoryStream GenerateDataExcel(DateTime? dateFrom, DateTime? dateTo, int offset)
        {
            var Query = GetReportQuery(dateFrom, dateTo, offset);
            Query = Query.OrderBy(b => b.UPONo);
            DataTable result = new DataTable();

            result.Columns.Add(new DataColumn() { ColumnName = "NOMOR NOTA KREDIT", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "TANGGAL NOTA KREDIT", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "KODE SUPPLIER", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "NAMA SUPPLIER", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "KATEGORI", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "NOMOR INVOICE", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "TANGGAL INVOICE", DataType = typeof(String) });

            result.Columns.Add(new DataColumn() { ColumnName = "TANGGAL JATUH TEMPO", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "KETERANGAN", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "PPN", DataType = typeof(string) });
            result.Columns.Add(new DataColumn() { ColumnName = "NOMOR FAKTUR PAJAK", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "TANGGAL FAKTUR PAJAK", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "PPH", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "JENIS PPH", DataType = typeof(String) });

            result.Columns.Add(new DataColumn() { ColumnName = "% PPH", DataType = typeof(double) });
            result.Columns.Add(new DataColumn() { ColumnName = "NOMOR FAKTUR PPH", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "TANGGAL FAKTUR PPH", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "NOMOR PO EXTERNAL", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "NOMOR PURCHASE REQUEST", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "NOMOR ACCOUNT", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "KODE BARANG", DataType = typeof(String) });

            result.Columns.Add(new DataColumn() { ColumnName = "NAMA BARANG", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "JUMLAH BARANG", DataType = typeof(double) });
            result.Columns.Add(new DataColumn() { ColumnName = "SATUAN BARANG", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "HARGA SATUAN BARANG", DataType = typeof(double) });
            result.Columns.Add(new DataColumn() { ColumnName = "INCLUDED PPN(Y / N)", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "MATA UANG", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "RATE", DataType = typeof(double) });

            result.Columns.Add(new DataColumn() { ColumnName = "HARGA TOTAL BARANG", DataType = typeof(double) });
            result.Columns.Add(new DataColumn() { ColumnName = "NOMOR BON TERIMA UNIT", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "TANGGAL BON TERIMA UNIT", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "PRINTED_FLAG", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "USER INPUT", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "TERM", DataType = typeof(string) });

            if (Query.ToArray().Count() == 0)
                result.Rows.Add("", "", "", "", "", "", "", "", "", "", "", "", "", "", 0, "", "", "", "", "", "", "", 0, "", 0, "", "", 0, 0, "", "", "", "", ""); // to allow column name to be generated properly for empty data as template
            else
            {
                var index = 0;
                foreach (var item in Query)
                {
                    index++;
                    string UPODate = item.UPODate == new DateTime(1970, 1, 1) ? "-" : item.UPODate.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd/MM/yyyy", new CultureInfo("id-ID"));
                    string InvoiceDate = item.InvoiceDate == null ? "-" : item.InvoiceDate.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd/MM/yyyy", new CultureInfo("id-ID"));
                    string DueDate = item.DueDate == new DateTime(1970, 1, 1) ? "-" : item.DueDate.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd/MM/yyyy", new CultureInfo("id-ID"));
                    string VatDate = item.VatDate == DateTimeOffset.MinValue ? "-" : item.VatDate.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd/MM/yyyy", new CultureInfo("id-ID"));
                    string IncomeTaxDate = item.IncomeTaxDate == DateTimeOffset.MinValue ? "-" : item.IncomeTaxDate.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd/MM/yyyy", new CultureInfo("id-ID"));
                    string URNDate = item.URNDate == null ? "-" : item.URNDate.GetValueOrDefault().ToOffset(new TimeSpan(offset, 0, 0)).ToString("dd/MM/yyyy", new CultureInfo("id-ID"));

                    result.Rows.Add(item.UPONo, UPODate, item.SupplierCode, item.SupplierName, item.CategoryName, item.InvoiceNo, InvoiceDate,
                                    DueDate, item.UPORemark, item.UseVat, item.VatNo, VatDate, item.UseIncomeTax, item.IncomeTaxName,
                                    item.IncomeTaxRate, item.IncomeTaxNo, IncomeTaxDate, item.EPONO, item.PRNo, item.AccountNo, item.ProductCode,
                                    item.ProductName, item.ReceiptQty, item.UOMUnit, item.PricePerDealUnit, item.IncludedPPN, item.CurrencyCode, item.CurrencyRate,
                                    item.PriceTotal, item.URNNo, URNDate, item.Printed, item.UserCreated, item.PaymentMethod);
                }
            }
            return Excel.CreateExcel(new List<KeyValuePair<DataTable, string>>() { new KeyValuePair<DataTable, string>(result, "Sheet1") }, true);
        }
    }
}
