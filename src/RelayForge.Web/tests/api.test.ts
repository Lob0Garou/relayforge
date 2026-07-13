import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../src/api';
describe('api client', () => {
  it('passes AbortSignal and decodes ProblemDetails', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({title:'Invalid page',detail:'Page is outside range',status:400}),{status:400,headers:{'Content-Type':'application/problem+json'}}));
    const api=createApi('http://api.test/',fetcher); const controller=new AbortController();
    await expect(api.events({page:1001},controller.signal)).rejects.toEqual(expect.objectContaining({message:'Page is outside range',status:400}));
    expect(fetcher).toHaveBeenCalledWith('http://api.test/api/events?page=1001',expect.objectContaining({signal:controller.signal}));
  });
  it('does not parse health response bodies', async () => {
    const fetcher=vi.fn().mockResolvedValue(new Response('',{status:200}));
    await expect(createApi('',fetcher).health()).resolves.toBe(true);
  });
});
