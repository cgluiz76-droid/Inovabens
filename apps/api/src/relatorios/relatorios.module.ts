import { Controller, Get, Module, Param, ParseUUIDPipe, Query, Res } from '@nestjs/common';
import type { Response } from 'express';
import { z } from 'zod';
import type { AuthUser } from '../common/auth.types';
import { ClientIp, CurrentUser, RequirePermission } from '../common/decorators';
import { ZodPipe } from '../common/zod.pipe';
import { FinanceiroModule } from '../financeiro/financeiro.module';
import { ImoveisModule } from '../imoveis/imoveis.module';
import { RelatoriosService } from './relatorios.service';

const UUID = new ParseUUIDPipe();
const PDF = 'application/pdf';
const XLSX = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
const idxQuery = z.object({ indice: z.enum(['IPCA', 'INCC', 'IGPM', 'SELIC']).default('IPCA') });

function enviar(res: Response, tipo: string, r: { buffer: Buffer; nome: string }) {
  res.set({ 'Content-Type': tipo, 'Content-Disposition': `attachment; filename="${r.nome}"`, 'Content-Length': String(r.buffer.length), 'Cache-Control': 'no-store' });
  res.send(r.buffer);
}

@Controller('imoveis/:id')
class RelatoriosController {
  constructor(private readonly s: RelatoriosService) {}

  @Get('ficha.pdf') @RequirePermission('imoveis:exportar')
  async fichaPdf(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @ClientIp() ip: string, @Res() res: Response) { enviar(res, PDF, await this.s.fichaImovelPdf(u, id, ip)); }
  @Get('ficha.xlsx') @RequirePermission('imoveis:exportar')
  async fichaXlsx(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @ClientIp() ip: string, @Res() res: Response) { enviar(res, XLSX, await this.s.fichaImovelXlsx(u, id, ip)); }

  @Get('ficha-financeira.pdf') @RequirePermission('financeiro:exportar')
  async finPdf(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Query(new ZodPipe(idxQuery)) q: z.infer<typeof idxQuery>, @ClientIp() ip: string, @Res() res: Response) { enviar(res, PDF, await this.s.fichaFinanceiraPdf(u, id, q.indice, ip)); }
  @Get('ficha-financeira.xlsx') @RequirePermission('financeiro:exportar')
  async finXlsx(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Query(new ZodPipe(idxQuery)) q: z.infer<typeof idxQuery>, @ClientIp() ip: string, @Res() res: Response) { enviar(res, XLSX, await this.s.fichaFinanceiraXlsx(u, id, q.indice, ip)); }
}

@Module({ imports: [ImoveisModule, FinanceiroModule], controllers: [RelatoriosController], providers: [RelatoriosService] })
export class RelatoriosModule {}
