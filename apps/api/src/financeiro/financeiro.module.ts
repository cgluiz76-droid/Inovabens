import { Body, Controller, Delete, Get, HttpCode, Module, Param, ParseUUIDPipe, Patch, Post, Put, Query } from '@nestjs/common';
import { z } from 'zod';
import { atualizarStatusLancamentoSchema } from '@inovabens/shared';
import type { AuthUser } from '../common/auth.types';
import { ClientIp, CurrentUser, RequirePermission } from '../common/decorators';
import { ZodPipe } from '../common/zod.pipe';
import { IndicesModule } from '../indices/indices.module';
import { AquisicaoService } from './aquisicao.service';
import { DespesasService } from './despesas.service';
import * as S from './financeiro.schemas';
import { ReceitasService } from './receitas.service';
import { RetornoService } from './retorno.service';

const UUID = new ParseUUIDPipe();

@Controller()
class FinanceiroController {
  constructor(
    private readonly aq: AquisicaoService, private readonly desp: DespesasService,
    private readonly rec: ReceitasService, private readonly ret: RetornoService,
  ) {}

  /* ---- Aquisição (CAPEX) ---- */
  @Get('imoveis/:id/aquisicao') @RequirePermission('financeiro:ver')
  obterAq(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.aq.obter(u, id); }
  @Put('imoveis/:id/aquisicao') @RequirePermission('financeiro:editar')
  salvarAq(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.aquisicaoSchema)) b: z.infer<typeof S.aquisicaoSchema>, @ClientIp() ip: string) { return this.aq.salvar(u, id, b, ip); }
  @Post('imoveis/:id/aquisicao/itens') @RequirePermission('financeiro:editar')
  criarItem(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.itemCriarSchema)) b: z.infer<typeof S.itemCriarSchema>, @ClientIp() ip: string) { return this.aq.criarItem(u, id, b, ip); }
  @Patch('aquisicao/itens/:id') @RequirePermission('financeiro:editar')
  atualizarItem(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.itemAtualizarSchema)) b: z.infer<typeof S.itemAtualizarSchema>, @ClientIp() ip: string) { return this.aq.atualizarItem(u, id, b, ip); }
  @Delete('aquisicao/itens/:id') @RequirePermission('financeiro:editar')
  excluirItem(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @ClientIp() ip: string) { return this.aq.excluirItem(u, id, ip); }

  /* ---- Categorias ---- */
  @Get('categorias-despesa') @RequirePermission('financeiro:ver')
  categorias() { return this.desp.categorias(true); }
  @Post('categorias-despesa') @RequirePermission('admin:config')
  criarCategoria(@Body(new ZodPipe(S.categoriaSchema)) b: z.infer<typeof S.categoriaSchema>) { return this.desp.criarCategoria(b); }
  @Patch('categorias-despesa/:id') @RequirePermission('admin:config')
  atualizarCategoria(@Param('id', UUID) id: string, @Body(new ZodPipe(S.categoriaPatchSchema)) b: z.infer<typeof S.categoriaPatchSchema>) { return this.desp.atualizarCategoria(id, b); }

  /* ---- Despesas (OPEX) e checklist mensal ---- */
  @Get('imoveis/:id/despesas') @RequirePermission('financeiro:ver')
  listarDesp(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.desp.listar(u, id); }
  @Post('imoveis/:id/despesas') @RequirePermission('financeiro:editar')
  criarDesp(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.despesaCriarSchema)) b: z.infer<typeof S.despesaCriarSchema>, @ClientIp() ip: string) { return this.desp.criar(u, id, b, ip); }
  @Patch('despesas/:id') @RequirePermission('financeiro:editar')
  atualizarDesp(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.despesaAtualizarSchema)) b: z.infer<typeof S.despesaAtualizarSchema>, @ClientIp() ip: string) { return this.desp.atualizar(u, id, b, ip); }
  @Delete('despesas/:id') @HttpCode(204) @RequirePermission('financeiro:editar')
  excluirDesp(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @ClientIp() ip: string) { return this.desp.excluir(u, id, ip); }
  @Get('imoveis/:id/despesas/checklist') @RequirePermission('financeiro:ver')
  checklist(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Query(new ZodPipe(S.checklistQuery)) q: z.infer<typeof S.checklistQuery>) { return this.desp.checklist(u, id, q.mes); }
  @Get('imoveis/:id/despesas/lancamentos') @RequirePermission('financeiro:ver')
  historico(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Query(new ZodPipe(S.historicoQuery)) q: z.infer<typeof S.historicoQuery>) { return this.desp.historico(u, id, q.de, q.ate); }
  @Patch('despesas/lancamentos/:id/status') @RequirePermission('financeiro:editar')
  status(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(atualizarStatusLancamentoSchema)) b: z.infer<typeof atualizarStatusLancamentoSchema>, @ClientIp() ip: string) { return this.desp.atualizarStatus(u, id, b, ip); }

  /* ---- Receitas e retorno ---- */
  @Get('imoveis/:id/receitas') @RequirePermission('financeiro:ver')
  listarRec(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.rec.listar(u, id); }
  @Post('imoveis/:id/receitas') @RequirePermission('financeiro:editar')
  criarRec(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.receitaCriarSchema)) b: z.infer<typeof S.receitaCriarSchema>, @ClientIp() ip: string) { return this.rec.criar(u, id, b, ip); }
  @Patch('receitas/:id') @RequirePermission('financeiro:editar')
  atualizarRec(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.receitaAtualizarSchema)) b: z.infer<typeof S.receitaAtualizarSchema>, @ClientIp() ip: string) { return this.rec.atualizar(u, id, b, ip); }
  @Delete('receitas/:id') @HttpCode(204) @RequirePermission('financeiro:editar')
  excluirRec(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @ClientIp() ip: string) { return this.rec.excluir(u, id, ip); }
  @Get('imoveis/:id/retorno') @RequirePermission('financeiro:ver')
  retorno(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Query(new ZodPipe(S.retornoQuery)) q: z.infer<typeof S.retornoQuery>) { return this.ret.calcular(u, id, q.indice, q.ate); }
}

@Module({
  imports: [IndicesModule],
  controllers: [FinanceiroController],
  providers: [AquisicaoService, DespesasService, ReceitasService, RetornoService],
  exports: [AquisicaoService, DespesasService, ReceitasService, RetornoService],
})
export class FinanceiroModule {}
