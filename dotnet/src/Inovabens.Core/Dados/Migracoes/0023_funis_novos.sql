-- Funis novos (fase 2 do CRM): Captação (lead = proprietário que oferece o imóvel) e os processos do imóvel
-- (Administrativo/Documentação e Projetos/Engenharia). Os valores novos só podem ser usados depois do commit,
-- por isso os estágios e os processos vêm no script seguinte.
ALTER TYPE "public"."funil" ADD VALUE IF NOT EXISTS 'CAPTACAO';
ALTER TYPE "public"."funil" ADD VALUE IF NOT EXISTS 'ADMINISTRATIVO';
ALTER TYPE "public"."funil" ADD VALUE IF NOT EXISTS 'PROJETOS';
ALTER TYPE "public"."finalidade_lead" ADD VALUE IF NOT EXISTS 'CAPTACAO';
