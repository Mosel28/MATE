﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Master40.Models;
using Microsoft.EntityFrameworkCore;
using Master40.DB.Models;
using Master40.DB.Data.Context;

namespace Master40.BusinessLogic.MRP
{
    public interface IProcessMrp
    {
        List<LogMessage> Logger { get; set; }
        Task Process(MrpTask task);
        void ProcessDemand(IDemandToProvider demand, MrpTask task);
    }

    public class ProcessMrp : IProcessMrp
    {
        private readonly MasterDBContext _context;
        public List<LogMessage> Logger { get; set; }
        public ProcessMrp(MasterDBContext context)
        {
            _context = context;
        }

        async Task IProcessMrp.Process(MrpTask task)
        {
           await Task.Run(() =>
           {
               IDemandToProvider demand;
               
               var orderParts = _context.OrderParts.Where(a => a.IsPlanned == false).Include(a => a.Article);
               if (task != MrpTask.GifflerThompson)
               {
                   foreach (var orderPart in orderParts.ToList())
                   {
                       var demandOrderParts = _context.Demands.OfType<DemandOrderPart>().Where(a => a.OrderPartId == orderPart.Id);
                       if (demandOrderParts.Any())
                       {
                           demand = demandOrderParts.First();
                       }
                       else
                       {
                           demand = CreateDemandOrderPart(orderPart);
                           //MRP I
                           ExecutePlanning(demand, null);
                           _context.SaveChanges();
                       }
                       
                       //MRP II
                       ProcessDemand(demand, task);
                   }
               }
               if ((task == MrpTask.All || task == MrpTask.GifflerThompson) && orderParts.Any())
               {
                   var capacity = new CapacityScheduling(_context);
                   capacity.GifflerThompsonScheduling();
                   foreach (var orderPart in orderParts.ToList())
                   {
                       orderPart.IsPlanned = true;
                   }
               }
               _context.SaveChanges();
           });
        }

        public void ProcessDemand(IDemandToProvider demand, MrpTask task)
        { 
            var schedule = new Scheduling(_context);
            if (task == MrpTask.All || task == MrpTask.Backward)
            {
                schedule.BackwardScheduling(demand);
                demand.State = State.BackwardScheduleExists;
            }


            if (task == MrpTask.All || task == MrpTask.Forward)
            {
                schedule.ForwardScheduling(demand);
                demand.State = State.ForwardScheduleExists;
            }
                
            _context.Demands.Update((DemandToProvider)demand);
            _context.SaveChanges();
        }

        private void ExecutePlanning(IDemandToProvider demand, 
                                     IDemandToProvider parent)
        {
            IDemandForecast demandForecast = new DemandForecast(_context);
            IScheduling schedule = new Scheduling(_context);

            var productionOrder = demandForecast.NetRequirement(demand, parent);
            demand.State = State.ProviderExist;

            foreach (var log in demandForecast.Logger)
            {
                Logger.Add(log);
            }

            if (productionOrder == null)
            {
                //there was enough in stock, so this does not have to be produced
                return;
            }
            schedule.CreateSchedule(demand, productionOrder);
            var children = _context.ArticleBoms
                                .Include(a => a.ArticleChild)
                                .ThenInclude(a => a.ArticleBoms)
                                .Where(a => a.ArticleParentId == demand.ArticleId)
                                .ToList();
            if (!children.Any())
            {
                return;
            }
            foreach (var child in children)
            {
                ExecutePlanning(new DemandProductionOrderBom()
                {
                    ProductionOrderBomId = child.Id,
                    ArticleId = child.ArticleChildId,
                    Article = child.ArticleChild,
                    Quantity = productionOrder.Quantity * (int) child.Quantity,
                    DemandRequesterId = demand.DemandRequesterId,
                    DemandProvider = new List<DemandToProvider>(),
                    State = State.Created
                }, demand);
            }
        }

        private IDemandToProvider CreateDemandOrderPart(OrderPart orderPart)
        {
            var demand = new DemandOrderPart()
            {
                OrderPartId = orderPart.Id,
                Quantity = orderPart.Quantity,
                Article = orderPart.Article,
                ArticleId = orderPart.ArticleId,
                OrderPart = orderPart,
                DemandProvider = new List<DemandToProvider>(),
                State = State.Created
            };
            _context.Demands.Add(demand);
            _context.SaveChanges();
            demand.DemandRequesterId = demand.Id;
            _context.Update(demand);
            _context.SaveChanges();
            return demand;
        }


    }

    public enum MrpTask
    {
        All,
        Forward,
        Backward,
        GifflerThompson
    }


}