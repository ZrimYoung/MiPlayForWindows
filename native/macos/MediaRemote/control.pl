#!/usr/bin/perl
use strict;
use warnings;
use DynaLoader;

die "Expected library path and command\n" unless @ARGV >= 2;
my ($library, $command, $target, $position) = @ARGV;
die "Library must be an absolute path\n" unless $library =~ m{^/};
my %symbols = (
    toggle => 'miplay_media_toggle', previous => 'miplay_media_previous',
    next => 'miplay_media_next', check => 'miplay_media_check',
    seek => 'miplay_media_seek',
    'target-check' => 'miplay_media_target_check',
);
die "Unsupported media command\n" unless exists $symbols{$command};
die "Unexpected arguments\n" unless @ARGV == ($command eq 'check' ? 2 : $command eq 'seek' ? 4 : 3);
delete @ENV{grep { /^MEDIAREMOTEADAPTER_/ || /^ADAPTER_TEST_MODE$/ || /^MIPLAY_MEDIA_/ } keys %ENV};
if ($command ne 'check') {
    die "Expected player identity\n" unless defined $target && $target =~ /^[A-Za-z0-9][A-Za-z0-9_.-]{0,255}$/;
    $ENV{MIPLAY_MEDIA_TARGET} = $target;
}
if ($command eq 'seek') {
    die "Invalid seek position\n" unless defined $position && $position =~ /^\d+(?:\.\d+)?$/ && $position <= 31536000;
    $ENV{MIPLAY_MEDIA_POSITION} = $position;
}
my $handle = DynaLoader::dl_load_file($library, 0) or die "Media library unavailable\n";
my $symbol = DynaLoader::dl_find_symbol($handle, $symbols{$command}) or die "Media control unavailable\n";
DynaLoader::dl_install_xsub('main::perform', $symbol);
perform();
