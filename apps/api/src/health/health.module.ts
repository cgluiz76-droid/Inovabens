import { Controller, Get, Inject, Module, ServiceUnavailableException } from '@nestjs/common';
import { SkipThrottle } from '@nestjs/throttler';
import { sql } from 'drizzle-orm';
import { Public } from '../common/decorators';
import { DB, Database } from '../db/db.module';

@Controller('health')
class HealthController {
  constructor(@Inject(DB) private readonly db: Database) {}

  /** Health check da nuvem (Render/Fly/ECS): verifica também a conexão com o banco. */
  @Public() @SkipThrottle() @Get()
  async check() {
    try { await this.db.execute(sql`select 1`); } catch { throw new ServiceUnavailableException('Banco indisponível'); }
    return { status: 'ok', db: 'up' };
  }
}

@Module({ controllers: [HealthController] })
export class HealthModule {}
