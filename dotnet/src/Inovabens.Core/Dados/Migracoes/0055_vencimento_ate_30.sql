-- Locação: dia de vencimento do aluguel aceita até 30 (antes até 28).
-- Em mês mais curto (fevereiro) o vencimento cai no último dia do mês — regra em CalculoLocacao.Vencimento.
ALTER TABLE "contratos_locacao" DROP CONSTRAINT IF EXISTS "contratos_locacao_dia_vencimento_check";
ALTER TABLE "contratos_locacao" ADD CONSTRAINT "contratos_locacao_dia_vencimento_check" CHECK ("dia_vencimento" BETWEEN 1 AND 30);
