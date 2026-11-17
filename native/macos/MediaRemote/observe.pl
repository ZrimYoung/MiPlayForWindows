#!/usr/bin/perl
# Loader adapted from mediaremote-adapter (BSD-3-Clause).
# Copyright (c) 2025 Jonas van den Berg and contributors. See vendor/LICENSE.
use strict;
use warnings;
use DynaLoader;

die "Expected one library path\n" unless @ARGV == 1;
my $library = shift @ARGV;
die "Library must be an absolute path\n" unless $library =~ m{^/};
# Neither caller environment nor arguments can select a media control command.
delete @ENV{grep { /^MEDIAREMOTEADAPTER_/ || /^ADAPTER_TEST_MODE$/ } keys %ENV};
$ENV{MEDIAREMOTEADAPTER_OPTION_no_diff} = '';
$ENV{MEDIAREMOTEADAPTER_OPTION_debounce} = '100';
my $handle = DynaLoader::dl_load_file($library, 0) or die "Media library unavailable\n";
my $symbol = DynaLoader::dl_find_symbol($handle, 'adapter_stream_env') or die "Observation unavailable\n";
DynaLoader::dl_install_xsub('main::observe', $symbol);
observe();
